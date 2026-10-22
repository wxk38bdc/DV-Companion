using System.Xml.Linq;
namespace DVCompanion;

public sealed record SavedProject(string Xml,string Backup,DateTimeOffset CapturedAt,int Modules);

public static class ProjectSave
{
    // A failed live read must never turn into a successful export of a stale editor copy.
    public static async Task<SavedProject> Current(string target,Func<Task<XDocument>> readCurrent)
    {
        var snapshot=await readCurrent();
        var capturedAt=DateTimeOffset.Now;
        var portable=ProjectRestore.Portable(snapshot);
        ProjectRestore.Order(portable);
        string xml=portable.ToString();
        string backup=Configuration.SaveAtomic(target,xml);
        return new(xml,backup,capturedAt,Configuration.Modules(portable).Count);
    }
}
