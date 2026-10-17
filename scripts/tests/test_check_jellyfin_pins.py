"""Tests for scripts/check_jellyfin_pins.py: python3 -m unittest discover -s scripts/tests"""
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))

import check_jellyfin_pins as pins  # noqa: E402

REPOSITORY = pathlib.Path(__file__).resolve().parents[2]

# A release workflow, its plugin and the plugin's unit tests, shaped like the repository's.
WORKFLOW = """\
env:
  PROJECT: 'Plugin/Plugin.csproj'
  TARGET_ABI: '{abi}'   # the Jellyfin it is built against
jobs:
  release:
    steps:
      - name: Build
        run: |
          dotnet publish "$PROJECT" -c Release -o out \\
            -p:JellyfinTarget={target} \\
            -p:Version="$VERSION"
      - name: Add the version to manifest.json
        run: |
          python3 scripts/update_manifest.py \\
            --version "$VERSION" \\
            --abi "$TARGET_ABI"
"""

PLUGIN = """\
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <JellyfinTarget Condition="'$(JellyfinTarget)' == ''">12</JellyfinTarget>
  </PropertyGroup>
  <PropertyGroup Condition="'$(JellyfinTarget)' == '12'">
    <JellyfinPackageVersion>12.0.0</JellyfinPackageVersion>
  </PropertyGroup>
  <PropertyGroup Condition="'$(JellyfinTarget)' == '10.11'">
    <JellyfinPackageVersion>10.11.11</JellyfinPackageVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Jellyfin.Controller" Version="$(JellyfinPackageVersion)" ExcludeAssets="runtime" />
    <PackageReference Include="Jellyfin.Model" Version="{model}" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
"""

TESTS = """\
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="Jellyfin.Controller" Version="{tests}" />
    <PackageReference Include="Jellyfin.Model" Version="{tests}" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../Plugin/Plugin.csproj" />
  </ItemGroup>
</Project>
"""


class CheckTests(unittest.TestCase):
    def empty(self) -> pathlib.Path:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        return pathlib.Path(directory.name)

    def repository(self, abi="12.0.0.0", target="12", model="$(JellyfinPackageVersion)",
                   tests="12.0.0") -> pathlib.Path:
        root = self.empty()
        files = {
            ".github/workflows/release-plugin.yml": WORKFLOW.format(abi=abi, target=target),
            "Plugin/Plugin.csproj": PLUGIN.format(model=model),
            "tests/Plugin.Tests/Plugin.Tests.csproj": TESTS.format(tests=tests),
        }
        for path, text in files.items():
            (root / path).parent.mkdir(parents=True, exist_ok=True)
            (root / path).write_text(text, encoding="utf-8")
        return root

    def test_matching_versions(self):
        self.assertEqual(pins.check(self.repository()), (
            ["release-plugin.yml: TARGET_ABI 12.0.0.0 matches Jellyfin 12.0.0 in Plugin/Plugin.csproj"
             " and tests/Plugin.Tests/Plugin.Tests.csproj"],
            [],
        ))

    def test_target_abi_must_be_the_jellyfin_the_plugin_builds_against(self):
        _, problems = pins.check(self.repository(abi="12.1.0.0"))
        self.assertEqual(problems, ["release-plugin.yml: TARGET_ABI is 12.1.0.0, but Plugin/Plugin.csproj builds"
                                    " against Jellyfin.Controller 12.0.0, so it should be 12.0.0.0"])

    def test_the_workflows_jellyfin_target_picks_the_version(self):
        _, problems = pins.check(self.repository(abi="10.11.11.0", target="10.11", tests="10.11.11"))
        self.assertEqual(problems, [])
        _, problems = pins.check(self.repository(abi="12.0.0.0", target="10.11", tests="10.11.11"))
        self.assertEqual(problems, ["release-plugin.yml: TARGET_ABI is 12.0.0.0, but Plugin/Plugin.csproj builds"
                                    " against Jellyfin.Controller 10.11.11, so it should be 10.11.11.0"])

    def test_controller_and_model_must_match(self):
        _, problems = pins.check(self.repository(model="12.1.0"))
        self.assertEqual(problems, ["release-plugin.yml: Plugin/Plugin.csproj builds against Jellyfin.Controller"
                                    " 12.0.0 but Jellyfin.Model 12.1.0"])

    def test_unit_tests_must_pin_the_same_version(self):
        _, problems = pins.check(self.repository(tests="12.0.*"))
        self.assertEqual(problems, [
            "release-plugin.yml: tests/Plugin.Tests/Plugin.Tests.csproj has Jellyfin.Controller 12.0.*, but"
            " Plugin/Plugin.csproj, which it tests, builds against 12.0.0",
            "release-plugin.yml: tests/Plugin.Tests/Plugin.Tests.csproj has Jellyfin.Model 12.0.*, but"
            " Plugin/Plugin.csproj, which it tests, builds against 12.0.0",
        ])

    def test_what_it_cannot_read_is_a_problem(self):
        root = self.repository()
        plugin = root / "Plugin/Plugin.csproj"
        text = plugin.read_text(encoding="utf-8").replace("'$(JellyfinTarget)' == '12'", "Exists('jellyfin.props')")
        plugin.write_text(text, encoding="utf-8")
        self.assertEqual(pins.check(root)[1], ["release-plugin.yml: Plugin/Plugin.csproj: can't read the condition"
                                               " Exists('jellyfin.props')"])

    def test_nothing_to_check_is_a_problem(self):
        self.assertEqual(pins.check(self.empty()), (
            [], ["No workflow in .github/workflows runs scripts/update_manifest.py, so there is nothing to check"]))

    def shared_code(self, other_target="12", shared_tests="12.0.0") -> pathlib.Path:
        """The repository with a second released plugin, Other, and shared/Lib, which both plugins compile in
        and tests/Lib.Tests tests on its own, as Medic Picks, Medic Profiles and tests/MedicShared.Tests do."""
        root = self.repository()
        compile_lib = '  <ItemGroup>\n    <Compile Include="..\\shared\\Lib\\*.cs" />\n  </ItemGroup>\n</Project>'
        plugin = PLUGIN.format(model="$(JellyfinPackageVersion)").replace("</Project>", compile_lib)
        files = {
            "Plugin/Plugin.csproj": plugin,
            "Other/Other.csproj": plugin,
            ".github/workflows/release-other.yml": WORKFLOW.format(
                abi="10.11.11.0" if other_target == "10.11" else "12.0.0.0",
                target=other_target).replace("Plugin/Plugin.csproj", "Other/Other.csproj"),
            "tests/Lib.Tests/Lib.Tests.csproj": TESTS.format(tests=shared_tests).replace(
                '<ProjectReference Include="../../Plugin/Plugin.csproj" />',
                '<Compile Include="../../shared/Lib/*.cs" />'),
        }
        for path, text in files.items():
            (root / path).parent.mkdir(parents=True, exist_ok=True)
            (root / path).write_text(text, encoding="utf-8")
        return root

    def test_shared_code_tests_are_compared_with_the_plugins_that_compile_it(self):
        self.assertEqual(pins.check(self.shared_code()), (
            ["release-other.yml: TARGET_ABI 12.0.0.0 matches Jellyfin 12.0.0 in Other/Other.csproj"
             " and tests/Lib.Tests/Lib.Tests.csproj",
             "release-plugin.yml: TARGET_ABI 12.0.0.0 matches Jellyfin 12.0.0 in Plugin/Plugin.csproj,"
             " tests/Lib.Tests/Lib.Tests.csproj and tests/Plugin.Tests/Plugin.Tests.csproj"],
            [],
        ))

    def test_shared_code_tests_must_pin_the_same_version(self):
        _, problems = pins.check(self.shared_code(shared_tests="12.1.0"))
        self.assertEqual(problems, [
            f"{workflow}: tests/Lib.Tests/Lib.Tests.csproj has {package} 12.1.0, but {plugin}, which compiles"
            " the shared/Lib it tests, builds against 12.0.0"
            for workflow, plugin in (("release-other.yml", "Other/Other.csproj"),
                                     ("release-plugin.yml", "Plugin/Plugin.csproj"))
            for package in pins.PACKAGES
        ])

    def test_plugins_that_compile_the_same_code_must_agree(self):
        _, problems = pins.check(self.shared_code(other_target="10.11"))
        self.assertIn("shared/Lib is compiled into Other/Other.csproj, which builds against Jellyfin 10.11.11,"
                      " and Plugin/Plugin.csproj, which builds against 12.0.0", problems)


# The unit test job runs this, which is what checks the repository on every pull request and release.
class RepositoryTests(unittest.TestCase):
    def test_releases_and_unit_tests_agree_on_jellyfin(self):
        _, problems = pins.check(REPOSITORY)
        self.assertEqual(problems, [])

    def test_the_shared_code_tests_are_compared(self):
        report, _ = pins.check(REPOSITORY)
        self.assertEqual([line for line in report if "MedicShared.Tests" in line], [
            "release-picks.yml: TARGET_ABI 12.0.0.0 matches Jellyfin 12.0.0 in"
            " Jellyfin.Plugin.MedicPicks/Jellyfin.Plugin.MedicPicks.csproj,"
            " tests/MedicPicks.Tests/MedicPicks.Tests.csproj and tests/MedicShared.Tests/MedicShared.Tests.csproj",
            "release-profiles.yml: TARGET_ABI 12.0.0.0 matches Jellyfin 12.0.0 in"
            " Jellyfin.Plugin.MedicProfiles/Jellyfin.Plugin.MedicProfiles.csproj,"
            " tests/MedicProfiles.Tests/MedicProfiles.Tests.csproj and"
            " tests/MedicShared.Tests/MedicShared.Tests.csproj",
        ])


if __name__ == "__main__":
    unittest.main()
