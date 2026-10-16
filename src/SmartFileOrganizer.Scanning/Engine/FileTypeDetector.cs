using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Scanning.Engine;

/// <summary>
/// Resolves file types from extension and (optionally) magic bytes.
/// </summary>
public static class FileTypeDetector
{
    private static readonly Dictionary<string, FileType> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images
        [".jpg"] = FileType.Image, [".jpeg"] = FileType.Image, [".png"] = FileType.Image,
        [".gif"] = FileType.Image, [".bmp"] = FileType.Image, [".tiff"] = FileType.Image,
        [".tif"] = FileType.Image, [".webp"] = FileType.Image, [".heic"] = FileType.Image,
        [".heif"] = FileType.Image, [".raw"] = FileType.Image, [".cr2"] = FileType.Image,
        [".nef"] = FileType.Image, [".arw"] = FileType.Image, [".svg"] = FileType.Image,
        [".ico"] = FileType.Image,
        // Video
        [".mp4"] = FileType.Video, [".mkv"] = FileType.Video, [".avi"] = FileType.Video,
        [".mov"] = FileType.Video, [".wmv"] = FileType.Video, [".flv"] = FileType.Video,
        [".webm"] = FileType.Video, [".m4v"] = FileType.Video, [".mpg"] = FileType.Video,
        [".mpeg"] = FileType.Video, [".ts"] = FileType.Video,
        // Audio
        [".mp3"] = FileType.Audio, [".wav"] = FileType.Audio, [".flac"] = FileType.Audio,
        [".aac"] = FileType.Audio, [".ogg"] = FileType.Audio, [".m4a"] = FileType.Audio,
        [".wma"] = FileType.Audio, [".opus"] = FileType.Audio,
        // Documents
        [".pdf"] = FileType.Document, [".doc"] = FileType.Document, [".docx"] = FileType.Document,
        [".xls"] = FileType.Document, [".xlsx"] = FileType.Document, [".ppt"] = FileType.Document,
        [".pptx"] = FileType.Document, [".odt"] = FileType.Document, [".ods"] = FileType.Document,
        [".txt"] = FileType.Document, [".rtf"] = FileType.Document, [".csv"] = FileType.Document,
        [".md"] = FileType.Document,
        // Archives
        [".zip"] = FileType.Archive, [".rar"] = FileType.Archive, [".7z"] = FileType.Archive,
        [".tar"] = FileType.Archive, [".gz"] = FileType.Archive, [".bz2"] = FileType.Archive,
        [".xz"] = FileType.Archive, [".iso"] = FileType.Archive,
        // Code
        [".cs"] = FileType.Code, [".js"] = FileType.Code, [".ts"] = FileType.Code,
        [".py"] = FileType.Code, [".java"] = FileType.Code, [".cpp"] = FileType.Code,
        [".c"] = FileType.Code, [".h"] = FileType.Code, [".go"] = FileType.Code,
        [".rs"] = FileType.Code, [".html"] = FileType.Code, [".css"] = FileType.Code,
        [".json"] = FileType.Code, [".xml"] = FileType.Code, [".yaml"] = FileType.Code,
        [".yml"] = FileType.Code, [".sh"] = FileType.Code, [".ps1"] = FileType.Code,
        [".sql"] = FileType.Code,
        // Executables
        [".exe"] = FileType.Executable, [".msi"] = FileType.Executable, [".dll"] = FileType.Executable,
        [".app"] = FileType.Executable, [".dmg"] = FileType.Executable, [".deb"] = FileType.Executable,
        [".rpm"] = FileType.Executable, [".apk"] = FileType.Executable,
        // Databases
        [".db"] = FileType.Database, [".sqlite"] = FileType.Database, [".sqlite3"] = FileType.Database,
        [".mdb"] = FileType.Database, [".accdb"] = FileType.Database,
        // Fonts
        [".ttf"] = FileType.Font, [".otf"] = FileType.Font, [".woff"] = FileType.Font,
        [".woff2"] = FileType.Font, [".eot"] = FileType.Font,
    };

    public static FileType Detect(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return FileType.Unknown;
        return ExtensionMap.TryGetValue(extension, out var type) ? type : FileType.Other;
    }
}
