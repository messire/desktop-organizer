namespace DesktopOrganizer.App;

internal class FileItem
{
    public string FilePath { get; set; }
    public string FileName => System.IO.Path.GetFileName(FilePath);
    public string FileType => System.IO.Path.GetExtension(FilePath);

    public override string ToString()
    {
        return FileName; // Отображение имени файла в списке
    }
}
