namespace WinDock.Services;

public enum SearchCategory { All, Applications, Documents, Folders, Images, Video, Audio }

public sealed record SearchFilter(SearchCategory Category = SearchCategory.All, string? Folder = null)
{
    public const string Documents = "pdf;doc;docx;docm;dot;dotx;rtf;txt;md;markdown;odt;pages;xls;xlsx;xlsm;csv;ods;numbers;ppt;pptx;pptm;odp;key;epub;tex";
    public const string Images = "jpg;jpeg;png;gif;webp;bmp;svg;tif;tiff;heic;heif;avif;ico;raw;psd";
    public const string Video = "mp4;mkv;mov;avi;wmv;webm;flv;m4v;mpeg;mpg;ts";
    public const string Audio = "mp3;wav;flac;aac;m4a;ogg;wma;opus;aiff";
    public bool IncludeApplications => Category == SearchCategory.Applications || Category == SearchCategory.All && string.IsNullOrEmpty(Folder);
    public string? Root => Category == SearchCategory.Applications || string.IsNullOrWhiteSpace(Folder) ? null : NormalizeRoot(Folder);

    public string Query(string query)
    {
        var type = Category switch
        {
            SearchCategory.Documents => "file: ext:" + Documents,
            SearchCategory.Folders => "folder:",
            SearchCategory.Images => "file: ext:" + Images,
            SearchCategory.Video => "file: ext:" + Video,
            SearchCategory.Audio => "file: ext:" + Audio,
            _ => ""
        };
        var root = Root is { } path ? "\"" + path + "\" " : "";
        return $"{root}{type} <{query}>".Trim();
    }

    public bool Accepts(SearchHit hit)
    {
        if (hit.Kind == "app") return IncludeApplications;
        if (Category == SearchCategory.Applications) return false;
        if (Root is { } root && !Path.GetFullPath(hit.Path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        return Category == SearchCategory.All || Category == SearchCategory.Folders && hit.Kind == "folder"
            || hit.Kind == "file" && CategoryOf(hit.Path) == Category;
    }

    public static SearchCategory CategoryOf(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.');
        if (Contains(Documents, extension)) return SearchCategory.Documents;
        if (Contains(Images, extension)) return SearchCategory.Images;
        if (Contains(Video, extension)) return SearchCategory.Video;
        if (Contains(Audio, extension)) return SearchCategory.Audio;
        return SearchCategory.All;
    }
    static bool Contains(string list, string extension) => list.Split(';').Contains(extension, StringComparer.OrdinalIgnoreCase);
    static string NormalizeRoot(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || folder.Contains('"') || folder.Any(char.IsControl))
            throw new ArgumentException(Loc.T("searchInvalidScope"));
        return Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }
}

public sealed record SearchVisual(string Glyph, string Color, string Label)
{
    public static SearchVisual For(SearchHit hit)
    {
        if (hit.Kind == "app") return new("\uE71D", "#ABD1FF", Loc.T("catPrograms"));
        if (hit.Kind == "folder") return new("\uE8B7", "#E8C887", Loc.T("searchFolders"));
        var extension = Path.GetExtension(hit.Path).TrimStart('.').ToLowerInvariant();
        if (extension == "pdf") return new("\uEA90", "#F19C9C", "PDF");
        if (extension is "doc" or "docx" or "docm") return new("\uE8A5", "#8BBEF4", "Word");
        if (extension is "xls" or "xlsx" or "xlsm" or "csv" or "ods") return new("\uE80A", "#8ED4B0", Loc.T("searchSpreadsheet"));
        if (extension is "ppt" or "pptx" or "pptm" or "odp") return new("\uE7F4", "#EFB18D", Loc.T("searchPresentation"));
        return SearchFilter.CategoryOf(hit.Path) switch
        {
            SearchCategory.Images => new("\uEB9F", "#C5ADF1", Loc.T("searchImages")),
            SearchCategory.Video => new("\uE714", "#ECABCB", Loc.T("searchVideo")),
            SearchCategory.Audio => new("\uE8D6", "#8CD0D3", Loc.T("searchAudio")),
            SearchCategory.Documents => new("\uE8A5", "#B5CAEA", Loc.T("searchDocuments")),
            _ when extension is "zip" or "7z" or "rar" or "tar" or "gz" => new("\uF012", "#E8C887", Loc.T("searchArchive")),
            _ when extension is "exe" or "msi" or "lnk" => new("\uE71D", "#ABD1FF", extension.ToUpperInvariant()),
            _ => new("\uE7C3", "#B1BAC8", extension.Length > 0 ? extension.ToUpperInvariant() : Loc.T("searchFiles"))
        };
    }
}
