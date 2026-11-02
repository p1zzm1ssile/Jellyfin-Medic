namespace JellyfinMedic.Services;

/// <summary>
/// ISO 639-2 language codes, so a language matches however a track or a setting writes it: in either 3-letter
/// form ("gre" and "ell" are both Greek), as its 2-letter code ("el"), or with a region ("pt-BR" is Portuguese).
/// Jellyfin lists a track by its terminology (T) form whatever the file says, while files and FFprobe mostly use
/// the bibliographic (B) form.
/// </summary>
internal static class LanguageCodes
{
    // Every code in the Library of Congress list as Jellyfin 12.2 ships it, in
    // Emby.Server.Implementations/Localization/iso6392.txt. Each entry is the T form, then the B form where it
    // differs, then the ISO 639-1 code where there is one. Jellyfin adds "ze" (bilingual Chinese) and "frc" (its
    // code for Canadian French, which matches only itself); its regional rows, such as "pt-br", need no entry, as
    // a region is ignored.
    private const string Iso6392 = """
        aar/aa abk/ab ace ach ada ady afa afh afr/af ain aka/ak akk ale alg alt amh/am ang anp apa ara/ar arc arg/an arn
        arp art arw asm/as ast ath aus ava/av ave/ae awa aym/ay aze/az
        bad bai bak/ba bal bam/bm ban bas bat bej bel/be bem ben/bn ber bho bih/bh bik bin bis/bi bla bnt bod/tib/bo
        bos/bs bra bre/br btk bua bug bul/bg byn
        cad cai car cat/ca cau ceb cel ces/cze/cs cha/ch chb che/ce chg chk chm chn cho chp chr chu/cu chv/cv chy cmc
        cop cor/kw cos/co cpe cpf cpp cre/cr crh crp csb cus cym/wel/cy
        dak dan/da dar day del den deu/ger/de dgr din div/dv doi dra dsb dua dum dyu dzo/dz
        efi egy eka ell/gre/el elx eng/en enm epo/eo est/et eus/baq/eu ewe/ee ewo
        fan fao/fo fas/per/fa fat fij/fj fil fin/fi fiu fon fra/fre/fr frc frm fro frr frs fry/fy ful/ff fur
        gaa gay gba gem gez gil gla/gd gle/ga glg/gl glv/gv gmh goh gon gor got grb grc grn/gn gsw guj/gu gwi
        hai hat/ht hau/ha haw heb/he her/hz hil him hin/hi hit hmn hmo/ho hrv/hr hsb hun/hu hup hye/arm/hy
        iba ibo/ig ido/io iii/ii ijo iku/iu ile/ie ilo ina/ia inc ind/id ine inh ipk/ik ira iro isl/ice/is ita/it
        jav/jv jbo jpn/ja jpr jrb
        kaa kab kac kal/kl kam kan/kn kar kas/ks kat/geo/ka kau/kr kaw kaz/kk kbd kha khi khm/km kho kik/ki kin/rw
        kir/ky kmb kok kom/kv kon/kg kor/ko kos kpe krc krl kro kru kua/kj kum kur/ku kut
        lad lah lam lao/lo lat/la lav/lv lez lim/li lin/ln lit/lt lol loz ltz/lb lua lub/lu lug/lg lui lun luo lus
        mad mag mah/mh mai mak mal/ml man map mar/mr mas mdf mdr men mga mic min mis mkd/mac/mk mkh mlg/mg mlt/mt mnc
        mni mno moh mon/mn mos mri/mao/mi msa/may/ms mul mun mus mwl mwr mya/bur/my myn myv
        nah nai nap nau/na nav/nv nbl/nr nde/nd ndo/ng nds nep/ne new nia nic niu nld/dut/nl nno/nn nob/nb nog non
        nor/no nqo nso nub nwc nya/ny nym nyn nyo nzi
        oci/oc oji/oj ori/or orm/om osa oss/os ota oto
        paa pag pal pam pan/pa pap pau peo phi phn pli/pi pol/pl pon por/pt pra pro pus/ps
        que/qu
        raj rap rar roa roh/rm rom ron/rum/ro run/rn rup rus/ru
        sad sag/sg sah sai sal sam san/sa sas sat scn sco sel sem sga sgn shn sid sin/si sio sit sla slk/slo/sk slv/sl
        sma sme/se smi smj smn smo/sm sms sna/sn snd/sd snk sog som/so son sot/st spa/es sqi/alb/sq srd/sc srn srp/sr
        srr ssa ssw/ss suk sun/su sus sux swa/sw swe/sv syc syr
        tah/ty tai tam/ta tat/tt tel/te tem ter tet tgk/tg tgl/tl tha/th tig tir/ti tiv tkl tlh tli tmh tog ton/to tpi
        tsi tsn/tn tso/ts tuk/tk tum tup tur/tr tut tvl twi/tw tyv
        udm uga uig/ug ukr/uk umb und urd/ur uzb/uz
        vai ven/ve vie/vi vol/vo vot
        wak wal war was wen wln/wa wol/wo
        xal xho/xh
        yao yap yid/yi yor/yo ypk
        zap zbl zen zgh zha/za zho/chi/zh/ze znd zul/zu zun zxx zza
        """;

    // ISO 639-1 replaced these in 1989, but older tools still write them.
    private static readonly Dictionary<string, string> Retired = new(StringComparer.Ordinal) { ["iw"] = "he", ["in"] = "id", ["ji"] = "yi" };

    // Bokmål and Nynorsk are both written Norwegian, so the three codes match each other. Each is still written
    // into files as itself.
    private static readonly Dictionary<string, string> MatchedAs = new(StringComparer.Ordinal) { ["nob"] = "nor", ["nno"] = "nor" };

    private static readonly Dictionary<string, (string T, string B)> Languages = Load();

    /// <summary>
    /// The form languages are compared in: the T form of a known code ("gre" and "el" are "ell"), and an unknown
    /// code as it is, so it still matches itself.
    /// </summary>
    internal static string Canonical(string code)
    {
        string clean = Clean(code);
        string t = Languages.TryGetValue(clean, out var language) ? language.T : clean;
        return MatchedAs.GetValueOrDefault(t, t);
    }

    /// <summary>Whether the code is in ISO 639-2 or ISO 639-1, with or without a region.</summary>
    internal static bool IsKnown(string code)
    {
        string clean = Clean(code);

        // ISO 639-2 keeps qaa to qtz for local use, so they're valid codes for no fixed language.
        return Languages.ContainsKey(clean) || clean is ['q', >= 'a' and <= 't', >= 'a' and <= 'z'];
    }

    /// <summary>The code to write into a file: the B form, which Matroska uses ("el" and "ell" are "gre"); an unknown code as it is.</summary>
    internal static string FileCode(string code)
    {
        string clean = Clean(code);
        return Languages.TryGetValue(clean, out var language) ? language.B : clean;
    }

    // Lower case, without a region ("pt-BR" and "pt_br" are "pt"), and with a retired code replaced.
    private static string Clean(string code)
    {
        string clean = code.Trim().ToLowerInvariant();
        int region = clean.AsSpan().IndexOfAny('-', '_');
        if (region > 0)
        {
            clean = clean[..region];
        }

        return Retired.GetValueOrDefault(clean, clean);
    }

    private static Dictionary<string, (string T, string B)> Load()
    {
        var languages = new Dictionary<string, (string T, string B)>(StringComparer.Ordinal);
        foreach (string entry in Iso6392.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] codes = entry.Split('/');
            string t = codes[0];
            string b = codes.Length > 1 && codes[1].Length == 3 ? codes[1] : t;
            foreach (string code in codes)
            {
                languages.Add(code, (t, b));
            }
        }

        return languages;
    }
}
