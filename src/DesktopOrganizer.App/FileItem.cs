using System.IO;

internal class FileItem
{
    public string FilePath { get; set; }
    public string FileName => Path.GetFileName(FilePath);
    public string FileType => Path.GetExtension(FilePath);

    public override string ToString()
    {
        return FileName;
    }
}
