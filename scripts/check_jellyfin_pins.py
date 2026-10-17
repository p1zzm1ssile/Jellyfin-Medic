#!/usr/bin/env python3
"""Checks that each release names the Jellyfin it is built against, and that its unit tests run on that Jellyfin.

The manifest's targetAbi is TARGET_ABI, typed into each release workflow, while the build takes its Jellyfin from the
plugin's csproj, and nothing tied the two together. For every workflow that runs scripts/update_manifest.py, this
checks that:

- TARGET_ABI is the plugin's Jellyfin.Controller version plus ".0" (12.1.0 needs 12.1.0.0), reading
  $(JellyfinPackageVersion) for the -p:JellyfinTarget the workflow builds with;
- Jellyfin.Model is at the same version as Jellyfin.Controller;
- each tests/*/*.Tests.csproj that references the plugin pins that same version, so its unit tests run on the
  Jellyfin the release is built against;
- each test project that compiles in a directory of code the plugin also compiles in (as tests/MedicShared.Tests
  does shared/Medic.Shared, which Medic Picks and Medic Profiles both ship) pins that same version too, and the
  plugins that compile in the same directory build against the same Jellyfin.

  python3 scripts/check_jellyfin_pins.py [repository]

The unit test job runs it through scripts/tests/test_check_jellyfin_pins.py. Prints a line for each release that
matches and, to stderr, each thing that doesn't, then exits 1 if there is any. It reads only the plain MSBuild the
projects use ($(Name), and conditions like '$(Name)' == 'value'), and reports anything else rather than guess.
"""
import argparse
import itertools
import os
import pathlib
import re
import shlex
import sys
import xml.etree.ElementTree as ET
from typing import NamedTuple

PACKAGES = ("Jellyfin.Controller", "Jellyfin.Model")
# Jellyfin's packages carry the server's three-number version. A pattern like 12.1.* would follow every new patch.
EXACT = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+")


class Unreadable(Exception):
    """Something the check can't read, so it can't vouch for that release."""


def scalar(raw: str) -> str:
    """A one-line YAML value, without its quotes or a trailing # comment."""
    raw = raw.strip()
    if raw[:1] in ("'", '"') and raw.find(raw[0], 1) > 0:
        return raw[1:raw.find(raw[0], 1)]
    return re.split(r"\s+#", raw, maxsplit=1)[0]


def workflow_env(text: str) -> dict[str, str]:
    """The values in a workflow's top-level env: block."""
    env: dict[str, str] = {}
    lines = iter(text.splitlines())
    for line in lines:
        if line.rstrip() == "env:":
            break
    for line in lines:
        if line[:1] not in ("", " ", "\t", "#"):
            break
        m = re.match(r"\s+([A-Za-z_]\w*):(.*)", line)
        if m:
            env[m[1]] = scalar(m[2])
    return env


def publish_command(text: str) -> list[str]:
    """The words of a workflow's one dotnet publish command."""
    lines = text.splitlines()
    starts = [i for i, line in enumerate(lines)
              if not line.lstrip().startswith("#") and re.search(r"\bdotnet\s+publish\b", line)]
    if len(starts) != 1:
        raise Unreadable(f"has {len(starts)} dotnet publish commands instead of one")
    first = starts[0]
    run = next((line for line in reversed(lines[:first + 1]) if re.match(r"\s*(- )?run:", line)), "")
    # In a "run: >" block YAML folds the lines below at the same indent into the command; in "run: |" the command
    # only goes on after a backslash.
    folded = re.match(r"\s*(- )?run:\s*>", run) is not None
    indent = len(lines[first]) - len(lines[first].lstrip())
    command = [lines[first].strip()]
    for line in lines[first + 1:]:
        if folded and (not line.strip() or len(line) - len(line.lstrip()) != indent):
            break
        if not folded and not command[-1].endswith("\\"):
            break
        command.append(line.strip())
    try:
        return shlex.split(" ".join(part.removesuffix("\\") for part in command))
    except ValueError as e:
        raise Unreadable(f"can't read its dotnet publish command: {e}") from None


def build(words: list[str], env: dict[str, str]) -> tuple[str, dict[str, str]]:
    """The project a dotnet publish builds and the -p properties it sets, with the workflow's env filled in."""
    words = [re.sub(r"\$\{(\w+)\}|\$(\w+)", lambda m: env.get(m[1] or m[2], m[0]), word) for word in words]
    projects = [word for word in words if word.endswith(".csproj")]
    if len(projects) != 1:
        raise Unreadable("can't tell which .csproj its dotnet publish builds")
    properties: dict[str, str] = {}
    for word in words:
        m = re.fullmatch(r"(?:--?|/)p(?:roperty)?:([^=]+)=(.*)", word)
        if m:
            properties[m[1].lower()] = m[2]
    return projects[0], properties


def local(element: ET.Element) -> str:
    """An element's tag without the XML namespace, which an MSBuild file may or may not declare."""
    return element.tag.rpartition("}")[2]


def expand(text: str, properties: dict[str, str]) -> str:
    """$(Name) filled in as MSBuild does, a property that isn't set being empty."""
    expanded = re.sub(r"\$\(([A-Za-z_][\w.-]*)\)", lambda m: properties.get(m[1].lower(), ""), text)
    if re.search(r"[$@%]\(", expanded):
        raise Unreadable(f"can't read {text}")
    return expanded


def holds(element: ET.Element, properties: dict[str, str]) -> bool:
    """Whether an element's Condition is true, comparing without case as MSBuild does."""
    condition = element.get("Condition")
    if condition is None:
        return True
    m = re.fullmatch(r"\s*'([^']*)'\s*(==|!=)\s*'([^']*)'\s*", condition)
    if not m:
        raise Unreadable(f"can't read the condition {condition}")
    return (expand(m[1], properties).lower() == expand(m[3], properties).lower()) == (m[2] == "==")


def source_directory(project: pathlib.Path, include: str) -> pathlib.Path:
    """The directory a Compile item takes its files from: the part of the path before any wildcard."""
    parts = pathlib.PurePosixPath(include.replace("\\", "/")).parts
    plain = list(itertools.takewhile(lambda part: not any(c in part for c in "*?"), parts))
    if len(plain) == len(parts):
        plain = plain[:-1]
    return project.parent.joinpath(*plain).resolve()


class Project(NamedTuple):
    """What the check reads from a .csproj."""
    packages: dict[str, str]  # Jellyfin package name to the version it references
    references: list[pathlib.Path]  # the projects it references
    compiles: set[pathlib.Path]  # the directories its Compile items take source files from


def evaluate(path: pathlib.Path, name: str, global_properties: dict[str, str]) -> Project:
    """The Jellyfin package versions a project references, the projects it references and the source it compiles in."""
    try:
        project = ET.parse(path).getroot()
        properties = dict(global_properties)
        # Every property before any item, as MSBuild evaluates them.
        for group in project:
            if local(group) != "PropertyGroup" or not holds(group, properties):
                continue
            for prop in group:
                # A property given on the command line wins over the project's, as in MSBuild.
                if local(prop).lower() not in global_properties and holds(prop, properties):
                    properties[local(prop).lower()] = expand(prop.text or "", properties)

        packages: dict[str, str] = {}
        references: list[pathlib.Path] = []
        compiles: set[pathlib.Path] = set()
        for group in project:
            if local(group) != "ItemGroup":
                continue
            for item in group:
                kind, include = local(item), item.get("Include", "")
                package = next((p for p in PACKAGES if p.lower() == include.lower()), None)
                if kind == "PackageReference" and package and holds(group, properties) and holds(item, properties):
                    version = item.get("Version") or next((v.text or "" for v in item if local(v) == "Version"), "")
                    packages[package] = expand(version, properties)
                elif kind == "ProjectReference" and holds(group, properties) and holds(item, properties):
                    references.append((path.parent / expand(include, properties).replace("\\", "/")).resolve())
                elif kind == "Compile" and include and holds(group, properties) and holds(item, properties):
                    compiles.update(source_directory(path, part.strip())
                                    for part in expand(include, properties).split(";") if part.strip())
    except OSError as e:
        raise Unreadable(f"{name}: {e.strerror}") from None
    except (ET.ParseError, Unreadable) as e:
        raise Unreadable(f"{name}: {e}") from None
    return Project(packages, references, compiles)


def release(text: str, root: pathlib.Path) -> tuple[str, pathlib.Path, Project]:
    """A release workflow's TARGET_ABI, the project it builds, and what that build references and compiles in."""
    env = workflow_env(text)
    if "TARGET_ABI" not in env:
        raise Unreadable("has no TARGET_ABI in its env")
    if not re.search(r'--abi\s+"?\$\{?TARGET_ABI\b', text):
        raise Unreadable("doesn't pass TARGET_ABI to scripts/update_manifest.py --abi")
    project, properties = build(publish_command(text), env)
    plugin = (root / project).resolve()
    return env["TARGET_ABI"], plugin, evaluate(plugin, project, properties)


def check(root: pathlib.Path) -> tuple[list[str], list[str]]:
    """One line for each release that matches or test project left out, and one for each problem."""
    root = root.resolve()
    report: list[str] = []
    problems: list[str] = []

    def name(path: pathlib.Path) -> str:
        return pathlib.Path(os.path.relpath(path, root)).as_posix()

    tests: dict[pathlib.Path, Project] = {}
    for path in sorted(root.glob("tests/*/*.Tests.csproj")):
        try:
            tests[path] = evaluate(path, name(path), {})
        except Unreadable as e:
            problems.append(str(e))

    paths = sorted([*root.glob(".github/workflows/*.yml"), *root.glob(".github/workflows/*.yaml")])
    workflows = {path: path.read_text(encoding="utf-8") for path in paths}
    workflows = {path: text for path, text in workflows.items() if "scripts/update_manifest.py" in text}
    if not workflows:
        problems.append("No workflow in .github/workflows runs scripts/update_manifest.py,"
                        " so there is nothing to check")

    compared: set[pathlib.Path] = set()
    # Each directory of source a released plugin compiles in, with the plugins and the Jellyfin they build against.
    compiled_by: dict[pathlib.Path, dict[pathlib.Path, str]] = {}
    for workflow, text in workflows.items():
        try:
            abi, plugin, built = release(text, root)
        except Unreadable as e:
            problems.append(f"{workflow.name}: {e}")
            continue
        packages = built.packages
        tested = [test for test, project in tests.items() if plugin in project.references]
        # Test projects that compile in code this plugin compiles in too, and the directory each shares with it.
        sharing = {test: min(project.compiles & built.compiles) for test, project in tests.items()
                   if test not in tested and project.compiles & built.compiles}
        compared.update(tested, sharing)

        found: list[str] = []
        version = packages.get("Jellyfin.Controller")
        if version is None:
            found.append(f"{name(plugin)} doesn't reference Jellyfin.Controller")
        elif not EXACT.fullmatch(version):
            found.append(f"{name(plugin)} builds against Jellyfin.Controller '{version}', not an exact version"
                         " like 12.1.0")
        else:
            model = packages.get("Jellyfin.Model", version)
            if model != version:
                found.append(f"{name(plugin)} builds against Jellyfin.Controller {version}"
                             f" but Jellyfin.Model {model}")
            if abi != version + ".0":
                found.append(f"TARGET_ABI is {abi}, but {name(plugin)} builds against Jellyfin.Controller {version},"
                             f" so it should be {version}.0")
            for test in tested:
                for package, pinned in tests[test][0].items():
                    if pinned != version:
                        found.append(f"{name(test)} has {package} {pinned}, but {name(plugin)}, which it tests,"
                                     f" builds against {version}")
            for test, directory in sharing.items():
                for package, pinned in tests[test].packages.items():
                    if pinned != version:
                        found.append(f"{name(test)} has {package} {pinned}, but {name(plugin)}, which compiles"
                                     f" the {name(directory)} it tests, builds against {version}")
            for directory in built.compiles:
                compiled_by.setdefault(directory, {})[plugin] = version

        if found:
            problems.extend(f"{workflow.name}: {problem}" for problem in found)
        else:
            files = [name(plugin), *sorted(name(path) for path in (*tested, *sharing))]
            listed = files[0] if len(files) == 1 else ", ".join(files[:-1]) + " and " + files[-1]
            report.append(f"{workflow.name}: TARGET_ABI {abi} matches Jellyfin {version} in {listed}")

    for directory, plugins in sorted(compiled_by.items()):
        if len(set(plugins.values())) > 1:
            (first, version), *others = sorted(plugins.items())
            rest = [f"{name(plugin)}, which builds against {other}" for plugin, other in others]
            builds = rest[0] if len(rest) == 1 else ", ".join(rest[:-1]) + " and " + rest[-1]
            problems.append(f"{name(directory)} is compiled into {name(first)}, which builds against Jellyfin"
                            f" {version}, and {builds}")

    for test in tests:
        if test not in compared:
            report.append(f"{name(test)} references none of the released plugins, so its Jellyfin isn't compared")
    return report, problems


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("repository", nargs="?", type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[1])
    args = ap.parse_args()

    report, problems = check(args.repository)
    for line in report:
        print(line)
    if problems:
        sys.exit("\n".join(problems))


if __name__ == "__main__":
    main()
