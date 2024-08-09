namespace BeSync.Models;

public class Language(string generalDisplayName, string originDisplayName, string languageCodeShort, string languageCodeLong)
{
    public static List<Language> AvailableLanguages { get; } = new List<Language>()
    {
        new Language("English", "English", "en", "eng"),
        new Language("French", "Français", "fr", "fre"),
        new Language("German", "Deutsch", "de", "deu"),
        new Language("Japanese", "日本語", "ja", "jpn"),
        new Language("Undefined", "Undefined", "und", "und"),
    };

    public static Language? LookupCode(string? code)
    {
        if (code == null)
            return null;
        
        return AvailableLanguages.FirstOrDefault(x => x.LanguageCodeLong == code.ToLower() || x.LanguageCodeShort == code.ToLower());
    }

    public string GeneralDisplayName { get; set; } = generalDisplayName;
    public string OriginDisplayName { get; set; } = originDisplayName;
    public string LanguageCodeShort { get; set; } = languageCodeShort;
    public string LanguageCodeLong { get; set; } = languageCodeLong;
    
    public override string ToString()
    {
        return $"{GeneralDisplayName} ({OriginDisplayName})";
    }
}