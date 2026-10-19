using System.IO;
using Bloom.Core;
using Bloom.Search;
using Bloom.Ui;

namespace Bloom.App;

/// Markdown files in Bloom's document window (Ui\DocWindow.cs): from Explorer (`--open`, DocHandoff), from the search.
internal sealed partial class Host
{
    /// The file's document window, made or brought forward; false for anything but a full path to a file that is there.
    public bool OpenDoc(string path)
    {
        if (!DocHandoff.Acceptable(path))
        {
            Log.Write("open: not a file " + path);
            return false;
        }
        Window.Dismiss();
        DocWindow.Show(path);
        Log.Write("open: " + path);
        return true;
    }

    /// A file from the search's results: a Markdown one in the document window when Windows opens .md files with this
    /// Bloom anyway (the user made it the default), anything else as Windows opens it.
    public void OpenFile(string path)
    {
        if (DocFile.IsMarkdown(path) && File.Exists(path) && FileAssoc.DefaultIsBloom()) OpenDoc(path);
        else Launch.Open(path);
    }
}
