// Read-only: is SOLIDWORKS ready for COM commands, and which documents are open in it?
// Exit code 0 = ready (prints the revision and the open documents), 1 = not running or not ready yet.
// PowerShell cannot call the SOLIDWORKS COM object itself (TYPE_E_ELEMENTNOTFOUND), hence this helper.
using System;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;

public static class SwDocs
{
    public static int Main()
    {
        try
        {
            SldWorks sw = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            Console.WriteLine("revision " + sw.RevisionNumber());
            object docs = sw.GetDocuments();
            int n = docs == null ? 0 : ((object[])docs).Length;
            Console.WriteLine("open documents: " + n);
            if (docs != null) foreach (object o in (object[])docs) { ModelDoc2 m = (ModelDoc2)o; Console.WriteLine("  " + m.GetPathName() + (m.GetSaveFlag() ? "  (unsaved changes)" : "")); }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("not ready: " + ex.Message); return 1; }
    }
}
