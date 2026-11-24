// Close every open SOLIDWORKS document WITHOUT saving (views and active studies were only looked at), then exit SOLIDWORKS.
// With the argument "keep", SOLIDWORKS stays open (documents closed only).
using System;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;

public static class CloseNoSave
{
    public static int Main(string[] a)
    {
        bool keep = a.Length > 0 && a[0] == "keep";
        SldWorks sw = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
        object docs = sw.GetDocuments();
        int n = docs == null ? 0 : ((object[])docs).Length;
        Console.WriteLine("open documents: " + n);
        if (docs != null) foreach (object o in (object[])docs) { ModelDoc2 m = (ModelDoc2)o; Console.WriteLine("  " + m.GetPathName() + (m.GetSaveFlag() ? " (unsaved view/state changes, discarded)" : "")); }
        sw.CloseAllDocuments(true);
        docs = sw.GetDocuments();
        n = docs == null ? 0 : ((object[])docs).Length;
        Console.WriteLine("after close: " + n);
        if (n == 0 && !keep) { sw.ExitApp(); Console.WriteLine("SOLIDWORKS closed"); }
        return 0;
    }
}
