// ShaftFea: linear static FEA of a shaft part in SOLIDWORKS Simulation, driven by a JSON case file.
//   ShaftFea.exe inspect <cad> [out.json]     describe the shaft: axis, ends, diameters along the axis, flats (nothing is changed)
//   ShaftFea.exe solve <case.json>            copy the CAD, add split circles, build the study, mesh, solve, report, save pictures
//   ShaftFea.exe sweep <case.json> [factors]  the same study at several mesh sizes (multiples of the default size), for convergence
// The CAD that is given is never modified: all work happens on a copy in the case's work folder.
// SOLIDWORKS must be running. Compiler: the .NET Framework csc (C# 5), so no ?. and no string interpolation.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.cosworks;

public static class ShaftFea
{
    const double MM = 0.001;
    static readonly CultureInfo IC = CultureInfo.InvariantCulture;
    static readonly string[] AX = { "X", "Y", "Z" };
    static List<string> Warnings = new List<string>();
    static void L(string s) { Console.WriteLine(s); }
    static void Warn(string s) { Warnings.Add(s); Console.WriteLine("WARNING: " + s); }
    static string N(double d, string f) { return d.ToString(f, IC); }

    // ---------- case file (JSON) ----------
    static Dictionary<string, object> ReadJson(string path)
    {
        var js = new JavaScriptSerializer(); js.MaxJsonLength = int.MaxValue;
        var d = js.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
        if (d == null) throw new Exception("the case file is not a JSON object: " + path);
        return d;
    }
    static bool Has(Dictionary<string, object> d, string k) { object o; return d != null && d.TryGetValue(k, out o) && o != null; }
    static double Dbl(object o) { return Convert.ToDouble(o, IC); }
    static double Num(Dictionary<string, object> d, string k, double def) { return Has(d, k) ? Dbl(d[k]) : def; }
    static double Need(Dictionary<string, object> d, string k, string what) { if (!Has(d, k)) throw new Exception(what + ": \"" + k + "\" is missing"); return Dbl(d[k]); }
    static string Str(Dictionary<string, object> d, string k, string def) { return Has(d, k) ? Convert.ToString(d[k], IC) : def; }
    static bool Flag(Dictionary<string, object> d, string k, bool def) { return Has(d, k) ? Convert.ToBoolean(d[k], IC) : def; }
    static Dictionary<string, object> Sub(Dictionary<string, object> d, string k) { return Has(d, k) ? d[k] as Dictionary<string, object> : null; }
    static List<object> Items(Dictionary<string, object> d, string k)
    {
        var r = new List<object>();
        if (!Has(d, k)) return r;
        var e = d[k] as IEnumerable;
        if (e != null && !(d[k] is string) && !(d[k] is IDictionary)) foreach (object o in e) r.Add(o); else r.Add(d[k]);
        return r;
    }
    static double[] Vec3(Dictionary<string, object> d, string k)
    {
        if (!Has(d, k)) return new double[] { 0, 0, 0 };
        double[] v = Items(d, k).Select(Dbl).ToArray();
        if (v.Length != 3) throw new Exception("\"" + k + "\" must be three numbers [X, Y, Z]");
        return v;
    }

    // ---------- SOLIDWORKS ----------
    static SldWorks Sw()
    {
        try { return (SldWorks)Marshal.GetActiveObject("SldWorks.Application"); }
        catch { throw new Exception("SOLIDWORKS is not running (or not ready yet)"); }
    }
    static CosmosWorks Cw(SldWorks sw)
    {
        object o = sw.GetAddInObject("SldWorks.Simulation");
        if (o == null)
        {
            string dll = Path.Combine(Path.GetDirectoryName(sw.GetExecutablePath().TrimEnd('\\') + "\\x"), "Simulation\\cosworks.dll");
            L("loading the Simulation add-in: " + sw.LoadAddIn(dll));
            o = sw.GetAddInObject("SldWorks.Simulation");
        }
        if (o == null) throw new Exception("the SOLIDWORKS Simulation add-in is not available");
        return ((CwAddincallback)o).CosmosWorks;
    }
    static ModelDoc2 OpenPart(SldWorks sw, string path, bool readOnly)
    {
        int err = 0, warn = 0;
        int opt = (int)swOpenDocOptions_e.swOpenDocOptions_Silent | (readOnly ? (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly : 0);
        ModelDoc2 m = sw.OpenDoc6(path, (int)swDocumentTypes_e.swDocPART, opt, "", ref err, ref warn);
        if (m == null) throw new Exception("cannot open " + path + " (error " + err + ")");
        int e2 = 0;
        sw.ActivateDoc3(m.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref e2);
        return m;
    }
    static void CloseIfOpen(SldWorks sw, string path)
    {
        ModelDoc2 d = (ModelDoc2)sw.GetOpenDocumentByName(path);
        if (d != null) sw.CloseDoc(d.GetTitle());
    }
    static bool IsNeutral(string ext) { return ext == ".step" || ext == ".stp" || ext == ".igs" || ext == ".iges" || ext == ".x_t" || ext == ".x_b" || ext == ".sat"; }

    // Open the CAD that was given, for reading only. A neutral file (STEP, IGES, Parasolid) is imported into a new, unsaved part.
    static ModelDoc2 OpenCad(SldWorks sw, string cad, out bool imported)
    {
        string ext = Path.GetExtension(cad).ToLowerInvariant();
        imported = false;
        if (ext == ".sldasm") throw new Exception("an assembly was given. This tool analyses one shaft part: give the shaft's own part file.");
        if (ext == ".sldprt") return OpenPart(sw, cad, true);
        if (!IsNeutral(ext)) throw new Exception("unsupported CAD type " + ext + " (use .SLDPRT, .STEP, .IGS or .X_T)");
        int err = 0;
        object data = (ext == ".step" || ext == ".stp") ? sw.GetImportFileData(cad) : null;
        ModelDoc2 m = (ModelDoc2)sw.LoadFile4(cad, "r", data, ref err);
        if (m == null) throw new Exception("cannot import " + cad + " (error " + err + ")");
        if (m.GetType() != (int)swDocumentTypes_e.swDocPART) throw new Exception("the imported file is not a single part");
        imported = true;
        return m;
    }

    // ---------- geometry ----------
    class Cyl { public Face2 F; public double R, A0, A1, Area; public bool Full, Inner; }
    class Flat { public double A0, A1, Dist; }
    class Geo
    {
        public int K;                                 // axis: 0 = X, 1 = Y, 2 = Z of the model
        public double[] C = new double[3];            // a point on the axis in mm (its component K is 0)
        public double Amin, Amax, Off, Volume;        // ends along the axis (model coordinate, mm); s = a - Off
        public int Bodies;
        public List<Cyl> Cyls = new List<Cyl>();      // cylindrical faces on the shaft axis
        public List<Flat> Flats = new List<Flat>();   // plane faces parallel to the axis (keyways, flats)
        public double Rmax { get { return Cyls.Where(c => !c.Inner).Max(c => c.R); } }
        public double Rout(double a) { var l = Cyls.Where(c => !c.Inner && a >= c.A0 - 1e-4 && a <= c.A1 + 1e-4).ToList(); return l.Count == 0 ? double.NaN : l.Max(c => c.R); }
        public double Rin(double a) { var l = Cyls.Where(c => c.Inner && a >= c.A0 - 1e-4 && a <= c.A1 + 1e-4).ToList(); return l.Count == 0 ? 0 : l.Max(c => c.R); }
        public double[] Point(double a) { double[] p = { C[0], C[1], C[2] }; p[K] = a; return p; }
        public double Radial(double[] p) { double s = 0; for (int i = 0; i < 3; i++) if (i != K) s += (p[i] - C[i]) * (p[i] - C[i]); return Math.Sqrt(s); }
    }

    // The shaft axis is the axis shared by the cylindrical faces that together span the longest length.
    static Geo Analyse(ModelDoc2 m, string axisPref)
    {
        object[] bodies = (object[])((PartDoc)m).GetBodies2((int)swBodyType_e.swSolidBody, true);
        if (bodies == null || bodies.Length == 0) throw new Exception("the part has no solid body");
        var g = new Geo(); g.Bodies = bodies.Length;
        double[] lo = { 1e9, 1e9, 1e9 }, hi = { -1e9, -1e9, -1e9 };
        var groups = new List<KeyValuePair<double[], List<Cyl>>>();   // key: k, cx, cy, cz
        var planes = new List<double[]>();
        int skew = 0;
        foreach (Body2 b in bodies)
        {
            double[] bb = (double[])b.GetBodyBox();
            for (int i = 0; i < 3; i++) { lo[i] = Math.Min(lo[i], bb[i] / MM); hi[i] = Math.Max(hi[i], bb[i + 3] / MM); }
            foreach (Face2 f in (object[])b.GetFaces())
            {
                Surface s = (Surface)f.GetSurface();
                double[] bx = (double[])f.GetBox();
                if (s.IsCylinder())
                {
                    double[] cp = (double[])s.CylinderParams;   // origin xyz, axis xyz, radius (m)
                    int k = -1;
                    for (int i = 0; i < 3; i++) if (Math.Abs(Math.Abs(cp[3 + i]) - 1) < 1e-6) k = i;
                    if (k < 0) { skew++; continue; }
                    double[] key = { k, cp[0] / MM, cp[1] / MM, cp[2] / MM }; key[1 + k] = 0;
                    var cyl = new Cyl { F = f, R = cp[6] / MM, A0 = bx[k] / MM, A1 = bx[k + 3] / MM, Area = f.GetArea() * 1e6 };
                    var grp = groups.FirstOrDefault(x => x.Key[0] == key[0] && Math.Abs(x.Key[1] - key[1]) < 0.01 && Math.Abs(x.Key[2] - key[2]) < 0.01 && Math.Abs(x.Key[3] - key[3]) < 0.01);
                    if (grp.Value == null) { grp = new KeyValuePair<double[], List<Cyl>>(key, new List<Cyl>()); groups.Add(grp); }
                    grp.Value.Add(cyl);
                }
                else if (s.IsPlane())
                {
                    double[] pp = (double[])s.PlaneParams;      // normal xyz, root point xyz (m)
                    planes.Add(new[] { pp[0], pp[1], pp[2], pp[3] / MM, pp[4] / MM, pp[5] / MM, bx[0] / MM, bx[1] / MM, bx[2] / MM, bx[3] / MM, bx[4] / MM, bx[5] / MM });
                }
            }
        }
        var cand = groups.AsEnumerable();
        if (!string.IsNullOrEmpty(axisPref) && axisPref.ToLowerInvariant() != "auto")
        {
            int want = Array.IndexOf(AX, axisPref.ToUpperInvariant());
            if (want < 0) throw new Exception("\"axis\" must be auto, X, Y or Z");
            cand = cand.Where(x => (int)x.Key[0] == want);
        }
        var list = cand.ToList();
        if (list.Count == 0) throw new Exception("no cylindrical face lies along a model axis" + (skew > 0 ? " (" + skew + " cylinders are skew to X, Y and Z: re-orient the part so its shaft axis is parallel to a model axis)" : ""));
        var best = list.OrderByDescending(x => x.Value.Max(c => c.A1) - x.Value.Min(c => c.A0)).ThenByDescending(x => x.Value.Sum(c => c.Area)).First();
        g.K = (int)best.Key[0];
        g.C = new[] { best.Key[1], best.Key[2], best.Key[3] };
        g.Amin = lo[g.K]; g.Amax = hi[g.K];
        g.Cyls = best.Value.OrderBy(c => c.A0).ThenBy(c => c.R).ToList();
        foreach (Cyl c in g.Cyls)
        {
            // a full circle may come as one face or, from an import, as two half faces over the same length: add those up
            double len = c.A1 - c.A0, full = 2 * Math.PI * c.R * len;
            double area = g.Cyls.Where(o => Math.Abs(o.R - c.R) < 1e-4 && Math.Abs(o.A0 - c.A0) < 1e-3 && Math.Abs(o.A1 - c.A1) < 1e-3).Sum(o => o.Area);
            c.Full = len > 1e-6 && Math.Abs(area - full) < 0.02 * full;
            // a bore: a larger cylinder on the same axis covers most of its length
            c.Inner = len > 1e-6 && g.Cyls.Any(o => o.R > c.R + 1e-4 && Math.Min(o.A1, c.A1) - Math.Max(o.A0, c.A0) > 0.6 * len);
        }
        foreach (double[] p in planes)
        {
            if (Math.Abs(p[g.K]) > 1e-6) continue;   // only planes parallel to the axis
            double dist = 0; for (int i = 0; i < 3; i++) if (i != g.K) dist += p[i] * (p[3 + i] - g.C[i]);
            g.Flats.Add(new Flat { A0 = p[6 + g.K], A1 = p[9 + g.K], Dist = Math.Abs(dist) });
        }
        try { g.Volume = m.Extension.CreateMassProperty().Volume * 1e9; } catch { }
        return g;
    }

    static double Offset(Geo g, Dictionary<string, object> c)
    {
        if (!Has(c, "origin")) return 0;
        object o = c["origin"];
        string s = o as string;
        if (s == null) return Dbl(o);
        s = s.ToLowerInvariant();
        if (s == "model") return 0;
        if (s == "min" || s == "start") return g.Amin;
        if (s == "max" || s == "end") return g.Amax;
        throw new Exception("\"origin\" must be model, min, max or a number (the model coordinate along the axis where s = 0)");
    }

    class Seg { public double A0, A1, R; public bool Full; }
    static List<Seg> Segments(Geo g, bool inner)
    {
        var res = new List<Seg>();
        foreach (Cyl c in g.Cyls.Where(x => x.Inner == inner && x.A1 - x.A0 > 1e-4).OrderBy(x => x.A0).ThenBy(x => x.R))
        {
            Seg last = res.LastOrDefault(s => Math.Abs(s.R - c.R) < 1e-4 && c.A0 <= s.A1 + 1e-3);
            if (last != null) { last.A1 = Math.Max(last.A1, c.A1); last.Full &= c.Full; }
            else res.Add(new Seg { A0 = c.A0, A1 = c.A1, R = c.R, Full = c.Full });
        }
        return res.OrderBy(s => s.A0).ToList();
    }

    static void Describe(Geo g)
    {
        int t1 = (g.K + 1) % 3, t2 = (g.K + 2) % 3;
        L(string.Format(IC, "shaft axis: model {0}, through {1} = {2:F3}, {3} = {4:F3} mm", AX[g.K], AX[t1], g.C[t1], AX[t2], g.C[t2]));
        L(string.Format(IC, "ends: {0} = {1:F3} .. {2:F3} mm, length {3:F3} mm", AX[g.K], g.Amin, g.Amax, g.Amax - g.Amin));
        L(string.Format(IC, "positions below are s = {0} - {1:F3} mm; s runs in the +{0} direction", AX[g.K], g.Off));
        if (g.Bodies > 1) L("NOTE: the part has " + g.Bodies + " solid bodies");
        if (g.Volume > 0) L(string.Format(IC, "volume {0:F1} mm3", g.Volume));
        L("outer diameters along the axis:");
        L("    s from       s to   length  diameter");
        foreach (Seg s in Segments(g, false))
            L(string.Format(IC, "  {0,9:F3}  {1,9:F3}  {2,7:F3}  {3,8:F3}{4}", s.A0 - g.Off, s.A1 - g.Off, s.A1 - s.A0, 2 * s.R, s.Full ? "" : "   (interrupted: keyway, flat or cross hole)"));
        var bores = Segments(g, true);
        if (bores.Count > 0)
        {
            L("bores on the axis:");
            foreach (Seg s in bores) L(string.Format(IC, "  {0,9:F3}  {1,9:F3}  {2,7:F3}  {3,8:F3}", s.A0 - g.Off, s.A1 - g.Off, s.A1 - s.A0, 2 * s.R));
        }
        var fl = g.Flats.GroupBy(f => N(f.A0, "F2") + "|" + N(f.A1, "F2")).Select(x => new Flat { A0 = x.First().A0, A1 = x.First().A1, Dist = x.Min(f => f.Dist) }).OrderBy(f => f.A0).ToList();
        if (fl.Count > 0)
        {
            L("plane faces parallel to the axis (keyways, flats), with the nearest distance to the axis:");
            foreach (Flat f in fl) L(string.Format(IC, "  {0,9:F3}  {1,9:F3}  {2,7:F3}  {3,8:F3}", f.A0 - g.Off, f.A1 - g.Off, f.A1 - f.A0, f.Dist));
        }
    }

    // Save a part in a neutral format chosen by the extension of the target (.step, .igs, .x_t), e.g. to hand a shaft to someone else.
    public static void Export(string part, string target)
    {
        SldWorks sw = Sw();
        bool wasOpen = sw.GetOpenDocumentByName(part) != null;
        ModelDoc2 m = OpenPart(sw, part, true);
        int e = 0, w = 0;
        bool ok = m.Extension.SaveAs3(target, 0, (int)swSaveAsOptions_e.swSaveAsOptions_Silent | (int)swSaveAsOptions_e.swSaveAsOptions_Copy, null, null, ref e, ref w);
        if (!wasOpen) sw.CloseDoc(m.GetTitle());
        if (!ok) throw new Exception("export failed (error " + e + ")");
        L("exported: " + target);
    }

    public static void Inspect(string cad, string outJson)
    {
        SldWorks sw = Sw();
        bool imported;
        bool wasOpen = sw.GetOpenDocumentByName(cad) != null;
        ModelDoc2 m = OpenCad(sw, cad, out imported);
        Geo g = Analyse(m, "auto");
        L("CAD: " + cad);
        Describe(g);
        if (outJson != null)
        {
            var d = new Dictionary<string, object>();
            d["cad"] = cad; d["axis"] = AX[g.K]; d["axisThrough"] = g.C; d["min"] = g.Amin; d["max"] = g.Amax; d["length"] = g.Amax - g.Amin;
            d["sections"] = Segments(g, false).Select(s => new Dictionary<string, object> { { "from", Math.Round(s.A0, 4) }, { "to", Math.Round(s.A1, 4) }, { "diameter", Math.Round(2 * s.R, 4) }, { "full", s.Full } }).ToArray();
            d["bores"] = Segments(g, true).Select(s => new Dictionary<string, object> { { "from", Math.Round(s.A0, 4) }, { "to", Math.Round(s.A1, 4) }, { "diameter", Math.Round(2 * s.R, 4) } }).ToArray();
            File.WriteAllText(outJson, new JavaScriptSerializer().Serialize(d));
            L("written: " + outJson);
        }
        if (imported || !wasOpen) sw.CloseDoc(m.GetTitle());   // leave SOLIDWORKS as it was found (an import is only a temporary part)
    }

    // ---------- the working copy ----------
    static ModelDoc2 MakeWorkPart(SldWorks sw, string cad, string workPart)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(workPart));
        CloseIfOpen(sw, workPart);
        string ext = Path.GetExtension(cad).ToLowerInvariant();
        if (ext == ".sldprt")
        {
            if (string.Equals(Path.GetFullPath(cad), Path.GetFullPath(workPart), StringComparison.OrdinalIgnoreCase)) throw new Exception("the work part would overwrite the CAD itself");
            File.Copy(cad, workPart, true);
            File.SetAttributes(workPart, FileAttributes.Normal);
            return OpenPart(sw, workPart, false);
        }
        bool imported;
        ModelDoc2 im = OpenCad(sw, cad, out imported);
        if (File.Exists(workPart)) File.Delete(workPart);
        int e = 0, w = 0;
        if (!im.Extension.SaveAs3(workPart, 0, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref e, ref w)) throw new Exception("cannot save the imported part as " + workPart + " (error " + e + ")");
        return im;
    }

    static Feature DefaultPlane(ModelDoc2 m, int index)   // 0 = Front (XY), 1 = Top (XZ), 2 = Right (YZ), whatever their names are
    {
        int i = 0;
        Feature f = (Feature)m.FirstFeature();
        while (f != null)
        {
            if (f.GetTypeName2() == "RefPlane") { if (i == index) return f; i++; }
            f = (Feature)f.GetNextFeature();
        }
        throw new Exception("the part has no default planes");
    }

    // Split circles at the given positions, so that load bands and probe sections get faces and node rings of their own.
    static int Split(SldWorks sw, ModelDoc2 m, Geo g, IEnumerable<double> stations)
    {
        var need = new List<double>();
        foreach (double a in stations)
            if (g.Cyls.Any(c => !c.Inner && a > c.A0 + 0.02 && a < c.A1 - 0.02) && !need.Any(x => Math.Abs(x - a) < 0.02)) need.Add(a);
        if (need.Count == 0) return 0;
        need.Sort();
        for (int i = 1; i < need.Count; i++) if (need[i] - need[i - 1] < 0.5) Warn(string.Format(IC, "split circles at s = {0:F3} and {1:F3} are only {2:F3} mm apart: expect sliver faces and a heavy mesh there", need[i - 1] - g.Off, need[i] - g.Off, need[i] - need[i - 1]));
        // a sketch plane that contains the axis direction; t is the in-plane direction across the shaft
        Feature plane = DefaultPlane(m, g.K == 2 ? 2 : 0);
        int t = g.K == 1 ? 0 : 1;
        m.ClearSelection2(true);
        if (!plane.Select2(false, 0)) throw new Exception("cannot select the sketch plane");
        m.SketchManager.InsertSketch(true);
        Sketch sk = (Sketch)m.SketchManager.ActiveSketch;
        if (sk == null) throw new Exception("the split sketch did not open");
        MathUtility mu = (MathUtility)sw.GetMathUtility();
        MathTransform tr = sk.ModelToSketchTransform;
        double half = g.Rmax * 1.3 + 2;
        m.SketchManager.AddToDB = true; m.SketchManager.DisplayWhenAdded = false;
        foreach (double a in need)
        {
            double[] p1 = { 0, 0, 0 }, p2 = { 0, 0, 0 };
            p1[g.K] = a * MM; p2[g.K] = a * MM; p1[t] = (g.C[t] - half) * MM; p2[t] = (g.C[t] + half) * MM;
            double[] q1 = (double[])((MathPoint)((MathPoint)mu.CreatePoint(p1)).MultiplyTransform(tr)).ArrayData;
            double[] q2 = (double[])((MathPoint)((MathPoint)mu.CreatePoint(p2)).MultiplyTransform(tr)).ArrayData;
            m.SketchManager.CreateLine(q1[0], q1[1], 0, q2[0], q2[1], 0);
        }
        m.SketchManager.AddToDB = false; m.SketchManager.DisplayWhenAdded = true;
        string skName = ((Feature)sk).Name;
        m.SketchManager.InsertSketch(true);
        m.ClearSelection2(true);
        if (!m.Extension.SelectByID2(skName, "SKETCH", 0, 0, 0, false, 4, null, 0)) throw new Exception("cannot select the split sketch");
        SelectionMgr sm = (SelectionMgr)m.SelectionManager;
        int n = 0;
        foreach (Cyl c in g.Cyls)
        {
            if (c.Inner || !need.Any(a => a > c.A0 + 0.02 && a < c.A1 - 0.02)) continue;
            SelectData sd = (SelectData)sm.CreateSelectData(); sd.Mark = 1;
            ((Entity)c.F).Select4(true, sd); n++;
        }
        m.InsertSplitLineProject(false, false);
        m.ClearSelection2(true);
        m.ForceRebuild3(false);
        L("split circles: " + need.Count + " positions on " + n + " faces");
        return need.Count;
    }

    static object[] Band(Geo g, double a0, double a1, string what)
    {
        object[] l = g.Cyls.Where(c => !c.Inner && c.A0 >= a0 - 0.02 && c.A1 <= a1 + 0.02 && c.A1 - c.A0 > 0.02).Select(c => (object)c.F).ToArray();
        if (l.Length == 0) throw new Exception(string.Format(IC, "{0}: there is no cylindrical face between s = {1} and {2}. Run \"inspect\" to see where the shaft's surfaces are.", what, a0 - g.Off, a1 - g.Off));
        return l;
    }

    // ---------- the case ----------
    class Station { public double A; public string Kind; }
    class Support { public string Name, Type; public double A0, A1, At; public bool Axial, Spin, SelfAlign; }
    class LoadDef { public string Name; public double A0, A1, At, T; public double[] F, M; }
    class Ctx
    {
        public Dictionary<string, object> Case;
        public string Name, Study, Cad, WorkDir, WorkPart;
        public SldWorks Sw; public ModelDoc2 M; public Geo G; public CWStudy St;
        public double Yield; public string Material;
        public List<Support> Supports = new List<Support>();
        public List<LoadDef> Loads = new List<LoadDef>();
        public List<Station> Stations = new List<Station>();
    }

    static string Safe(string s) { foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s.Trim(); }
    static string Abs(string p, string baseDir) { p = p.Replace('/', '\\'); return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(baseDir, p)); }

    static Ctx Build(string caseFile)
    {
        var x = new Ctx();
        caseFile = Path.GetFullPath(caseFile);
        string baseDir = Path.GetDirectoryName(caseFile);
        var c = x.Case = ReadJson(caseFile);
        x.Name = Safe(Str(c, "name", Path.GetFileNameWithoutExtension(caseFile)));
        x.Study = Str(c, "study", "Static");
        if (!Has(c, "cad")) throw new Exception("the case has no \"cad\" path");
        x.Cad = Abs(Str(c, "cad", ""), baseDir);
        if (!File.Exists(x.Cad)) throw new Exception("CAD file not found: " + x.Cad);
        x.WorkDir = Has(c, "workDir") ? Abs(Str(c, "workDir", ""), baseDir) : Path.Combine(baseDir, x.Name + " FEA");
        x.WorkPart = Path.Combine(x.WorkDir, x.Name + " (FEA).SLDPRT");
        L("case: " + x.Name + "   study: " + x.Study);
        L("CAD: " + x.Cad);
        L("work part: " + x.WorkPart);

        x.Sw = Sw();
        x.M = MakeWorkPart(x.Sw, x.Cad, x.WorkPart);
        string axis = Str(c, "axis", "auto");
        x.G = Analyse(x.M, axis);
        x.G.Off = Offset(x.G, c);
        Describe(x.G);
        Geo g = x.G;

        // supports and loads, positions converted from s to the model coordinate
        int i = 0;
        foreach (object o in Items(c, "supports"))
        {
            var d = o as Dictionary<string, object>; i++;
            var s = new Support { Name = Str(d, "name", "support " + i), Type = Str(d, "type", "bearing").ToLowerInvariant() };
            if (s.Type != "bearing" && s.Type != "fixed") throw new Exception(s.Name + ": type must be \"bearing\" or \"fixed\"");
            s.A0 = Need(d, "from", s.Name) + g.Off; s.A1 = Need(d, "to", s.Name) + g.Off;
            if (s.A1 <= s.A0) throw new Exception(s.Name + ": \"to\" must be greater than \"from\"");
            s.At = Has(d, "at") ? Dbl(d["at"]) + g.Off : (s.A0 + s.A1) / 2;
            s.Axial = Flag(d, "axial", false); s.Spin = Flag(d, "spin", false); s.SelfAlign = Flag(d, "selfAlign", true);
            x.Supports.Add(s);
        }
        if (x.Supports.Count == 0) throw new Exception("the case has no supports");
        // a free rigid-body motion makes the solve fail: something has to hold the shaft along its axis and about it
        if (!x.Supports.Any(s => s.Type == "fixed"))
        {
            if (!x.Supports.Any(s => s.Axial)) { x.Supports[0].Axial = true; L("note: no support holds the shaft axially, so \"" + x.Supports[0].Name + "\" does (\"axial\": true)"); }
            if (!x.Supports.Any(s => s.Spin)) { x.Supports[0].Spin = true; L("note: no support holds rotation about the axis, so \"" + x.Supports[0].Name + "\" does (\"spin\": true)"); }
            if (x.Supports.Count < 2 && x.Supports[0].SelfAlign) Warn("one self-aligning bearing cannot hold a shaft: add a second support, or use \"type\": \"fixed\" or \"selfAlign\": false");
        }
        i = 0;
        double[] sumF = { 0, 0, 0 }; double sumT = 0;
        foreach (object o in Items(c, "loads"))
        {
            var d = o as Dictionary<string, object>; i++;
            var ld = new LoadDef { Name = Str(d, "name", "load " + i), F = Vec3(d, "force"), M = Vec3(d, "moment"), T = Num(d, "torque", 0) };
            ld.At = Need(d, "at", ld.Name) + g.Off;
            if (Has(d, "width")) { double w = Dbl(d["width"]); ld.A0 = ld.At - w / 2; ld.A1 = ld.At + w / 2; }
            else { ld.A0 = Need(d, "from", ld.Name) + g.Off; ld.A1 = Need(d, "to", ld.Name) + g.Off; }
            if (ld.A1 <= ld.A0) throw new Exception(ld.Name + ": \"to\" must be greater than \"from\"");
            for (int k = 0; k < 3; k++) sumF[k] += ld.F[k];
            sumT += ld.T + ld.M[g.K];
            x.Loads.Add(ld);
        }
        if (x.Loads.Count == 0) throw new Exception("the case has no loads");
        L(string.Format(IC, "applied force sum: ({0:F4}, {1:F4}, {2:F4}) N; torque sum about the axis: {3:F3} N.mm", sumF[0], sumF[1], sumF[2], sumT));
        if (Math.Abs(sumT) > 1e-6 * Math.Max(1, x.Loads.Max(l => Math.Abs(l.T)))) L("note: the torques do not cancel; the remainder is carried by the support that holds rotation about the axis");

        // stations: band edges, load points and probes all get a split circle
        Action<double, string> add = (a, kind) => { if (!x.Stations.Any(s => Math.Abs(s.A - a) < 0.02)) x.Stations.Add(new Station { A = a, Kind = kind }); };
        foreach (object o in Items(c, "probes")) add(Dbl(o) + g.Off, "probe");
        foreach (LoadDef ld in x.Loads) { add(ld.At, "load: " + ld.Name); add(ld.A0, "edge of " + ld.Name); add(ld.A1, "edge of " + ld.Name); }
        foreach (Support s in x.Supports) { add(s.A0, "edge of " + s.Name); add(s.A1, "edge of " + s.Name); }
        foreach (Station s in x.Stations) if (s.A < g.Amin - 0.02 || s.A > g.Amax + 0.02) throw new Exception(string.Format(IC, "position s = {0} ({1}) is outside the shaft, which runs from s = {2:F3} to {3:F3}", s.A - g.Off, s.Kind, g.Amin - g.Off, g.Amax - g.Off));
        x.Stations = x.Stations.OrderBy(s => s.A).ToList();

        if (Split(x.Sw, x.M, g, x.Stations.Select(s => s.A)) > 0) { double off = g.Off; x.G = g = Analyse(x.M, AX[g.K]); g.Off = off; }

        // ---- study ----
        CosmosWorks cw = Cw(x.Sw);
        CWStudyManager sm = cw.ActiveDoc.StudyManager;
        int err;
        for (int k = sm.StudyCount - 1; k >= 0; k--) { CWStudy old = sm.GetStudy(k); if (old != null && old.Name == x.Study) sm.DeleteStudy(x.Study); }
        CWStudy st = x.St = sm.CreateNewStudy3(x.Study, (int)swsAnalysisStudyType_e.swsAnalysisStudyTypeStatic, 0, out err);
        if (st == null) throw new Exception("the study was not created (error " + err + ")");
        try { st.StaticStudyOptions.ResultFolder = x.WorkDir; } catch (Exception ex) { Warn("result folder not set: " + ex.Message); }

        // material
        var md = Sub(c, "material");
        if (md == null) throw new Exception("the case has no \"material\"");
        CWSolidManager sol = st.SolidManager;
        for (int ci = 0; ci < sol.ComponentCount; ci++)
        {
            CWSolidComponent sc = sol.GetComponentAt(ci, out err);
            for (int bi = 0; bi < sc.SolidBodyCount; bi++)
            {
                CWSolidBody body = sc.GetSolidBodyAt(bi, out err);
                if (Has(md, "library"))
                {
                    string lib = Path.Combine(Path.GetDirectoryName(x.Sw.GetExecutablePath().TrimEnd('\\') + "\\x"), "lang\\english\\sldmaterials\\solidworks materials.sldmat");
                    x.Material = Str(md, "library", "");
                    if (!body.SetLibraryMaterial2(lib, x.Material)) throw new Exception("library material not found: " + x.Material);
                    int bt; x.Yield = Convert.ToDouble(body.GetSolidBodyMaterial().GetPropertyByName(0, "SIGYLD", out bt), IC) / 1e6;
                    if (Has(md, "yield_MPa")) x.Yield = Dbl(md["yield_MPa"]);
                }
                else
                {
                    CWMaterial mat = body.GetDefaultMaterial();
                    mat.MaterialUnits = (int)swsUnitSystem_e.swsUnitSystemSI;
                    x.Material = mat.MaterialName = Str(md, "name", "custom");
                    mat.SetPropertyByName("EX", Need(md, "E_MPa", "material") * 1e6, 0);
                    mat.SetPropertyByName("NUXY", Need(md, "nu", "material"), 0);
                    mat.SetPropertyByName("DENS", Num(md, "density", 7850), 0);
                    x.Yield = Need(md, "yield_MPa", "material");
                    mat.SetPropertyByName("SIGYLD", x.Yield * 1e6, 0);
                    mat.SetPropertyByName("SIGXT", Num(md, "tensile_MPa", x.Yield) * 1e6, 0);
                    int me = body.SetSolidBodyMaterial(mat);
                    if (me != 0) throw new Exception("the material was not accepted (error " + me + ")");
                }
            }
        }
        L(string.Format(IC, "material: {0}, yield {1:F1} MPa", x.Material, x.Yield));

        // supports
        CWLoadsAndRestraintsManager lm = st.LoadsAndRestraintsManager;
        foreach (Support s in x.Supports)
        {
            object[] faces = Band(g, s.A0, s.A1, s.Name);
            if (s.Type == "fixed")
            {
                CWRestraint r = lm.AddRestraint((int)swsRestraintType_e.swsRestraintTypeFixed, faces, null, out err);
                if (r == null || err != 0) throw new Exception(s.Name + ": fixed support failed (error " + err + ")");
                L(string.Format(IC, "support \"{0}\": fixed, s {1} .. {2}, {3} face(s)", s.Name, s.A0 - g.Off, s.A1 - g.Off, faces.Length));
                continue;
            }
            // A bearing: the seat is tied rigidly to a point on the axis. That point cannot translate (radially always, axially on
            // request) but may tilt freely, as a single-row ball bearing carries no moment. selfAlign false also holds the tilt.
            // The seat keeps its shape, like a shaft inside an inner ring. (A distributed coupling for a displacement was tried:
            // SOLIDWORKS 2025 refuses the load type DirectDisplacement here, and with ConnectionType = Distributed the solve fails.)
            double[] p = g.Point(s.At);
            CWRemoteLoad rl = lm.AddRemoteLoad((int)swsRemoteLoadType_e.swsRemoteLoadType_RigidDisplacement, faces, (int)swsLinearUnit_e.swsLinearUnitMillimeters, p[0], p[1], p[2], out err);
            if (rl == null) throw new Exception(s.Name + ": bearing support failed (error " + err + ")");
            bool[] tb = { true, true, true }; tb[g.K] = s.Axial;
            bool[] rb = { !s.SelfAlign, !s.SelfAlign, !s.SelfAlign }; rb[g.K] = s.Spin;
            rl.RemoteLoadBeginEdit();
            rl.SetReferenceCoordinateSystem2(true, null);
            rl.SetForceOrTranslationValues2(true, tb[0], 0, tb[1], 0, tb[2], 0);
            rl.SetMomentOrRotationValues2(rb[0] || rb[1] || rb[2], rb[0], 0, rb[1], 0, rb[2], 0);
            int e2 = rl.RemoteLoadEndEdit();
            if (err != 0 || e2 != 0) throw new Exception(s.Name + ": bearing support failed (errors " + err + ", " + e2 + ")");
            L(string.Format(IC, "support \"{0}\": bearing, s {1} .. {2}, centre {3}, {4} face(s){5}{6}{7}", s.Name, s.A0 - g.Off, s.A1 - g.Off, s.At - g.Off, faces.Length, s.Axial ? ", axial held" : "", s.Spin ? ", spin held" : "", s.SelfAlign ? "" : ", tilt held"));
        }
        // loads
        foreach (LoadDef ld in x.Loads)
        {
            object[] faces = Band(g, ld.A0, ld.A1, ld.Name);
            double[] p = g.Point(ld.At);
            CWRemoteLoad rl = lm.AddRemoteLoad((int)swsRemoteLoadType_e.swsRemoteLoadType_DirectLoad, faces, (int)swsLinearUnit_e.swsLinearUnitMillimeters, p[0], p[1], p[2], out err);
            if (rl == null) throw new Exception(ld.Name + ": load failed (error " + err + ")");
            double[] mo = { ld.M[0], ld.M[1], ld.M[2] }; mo[g.K] += ld.T;
            bool[] fb = ld.F.Select(v => Math.Abs(v) > 1e-12).ToArray(), mb = mo.Select(v => Math.Abs(v) > 1e-12).ToArray();
            rl.RemoteLoadBeginEdit();
            rl.SetReferenceCoordinateSystem2(true, null);
            rl.ForceOrTranslationUnit = (int)swsForceUnit_e.swsForceUnitNOrNm;
            rl.MomentOrRotationUnit = (int)swsForceUnit_e.swsForceUnitNOrNm;
            rl.SetForceOrTranslationValues2(fb[0] || fb[1] || fb[2], fb[0], ld.F[0], fb[1], ld.F[1], fb[2], ld.F[2]);
            rl.SetMomentOrRotationValues2(mb[0] || mb[1] || mb[2], mb[0], mo[0] / 1000.0, mb[1], mo[1] / 1000.0, mb[2], mo[2] / 1000.0);
            int e2 = rl.RemoteLoadEndEdit();
            if (err != 0 || e2 != 0) throw new Exception(ld.Name + ": load failed (errors " + err + ", " + e2 + ")");
            L(string.Format(IC, "load \"{0}\": at s = {1}, on s {2} .. {3} ({4} face(s)), F = ({5:F4}, {6:F4}, {7:F4}) N, M = ({8:F3}, {9:F3}, {10:F3}) N.mm", ld.Name, ld.At - g.Off, ld.A0 - g.Off, ld.A1 - g.Off, faces.Length, ld.F[0], ld.F[1], ld.F[2], mo[0], mo[1], mo[2]));
        }
        Save(x.M);
        return x;
    }

    static void Save(ModelDoc2 m)
    {
        int e = 0, w = 0;
        if (!m.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref e, ref w)) Warn("the work part was not saved (error " + e + ")");
    }

    // ---------- mesh, solve, results ----------
    static double[] Arr(object o)
    {
        if (o == null) return new double[0];
        Array a = (Array)o; double[] d = new double[a.Length]; int i = 0;
        foreach (object v in a) d[i++] = Convert.ToDouble(v, IC);
        return d;
    }

    class StRes { public Station S; public double R, Ut, Vm = -1, Moment = double.NaN; public double[] U = new double[3]; public int Nodes; public bool Slice; }
    class Res
    {
        public double MaxSize, MinSize, DefMax, DefMin, WorstJac, MaxAR, Seconds;
        public int Elements, Nodes;
        public double UMax, UMaxA, UMaxR, VMax, VMaxA, VMaxR;
        public double[] VMaxP = new double[3];
        public List<StRes> St = new List<StRes>();
    }

    static Res MeshRun(Ctx x, double maxMm, double minMm, double factor)
    {
        Geo g = x.G; CWStudy st = x.St;
        var r = new Res();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        CWMesh mesh = st.Mesh;
        mesh.MesherType = (int)swsMesherType_e.swsMesherTypeAlternateCB;   // blended curvature-based
        mesh.Quality = (int)swsMeshQuality_e.swsMeshQualityHigh;           // 10-node tetrahedra
        mesh.GetDefaultMaxAndMinElementSize((int)swsLinearUnit_e.swsLinearUnitMillimeters, out r.DefMax, out r.DefMin);
        if (maxMm <= 0) { maxMm = r.DefMax * factor; minMm = r.DefMin * factor; }
        if (minMm <= 0) minMm = maxMm / 3;
        r.MaxSize = maxMm; r.MinSize = minMm;
        int me = st.CreateMesh((int)swsLinearUnit_e.swsLinearUnitMillimeters, maxMm, minMm);
        if (me != 0) throw new Exception("meshing failed (error " + me + ") at element size " + N(maxMm, "F4") + " / " + N(minMm, "F4") + " mm");
        r.Elements = mesh.ElementCount; r.Nodes = mesh.NodeCount;
        try { r.WorstJac = mesh.GetWorstJacobianRatio(); r.MaxAR = mesh.MaxAspectRatio; } catch { }
        L(string.Format(IC, "mesh: max {0:F4} min {1:F4} mm (default {2:F4} / {3:F4}), {4} elements, {5} nodes, worst Jacobian {6:F2}, max aspect ratio {7:F2}", maxMm, minMm, r.DefMax, r.DefMin, r.Elements, r.Nodes, r.WorstJac, r.MaxAR));
        // not set by this tool: they are the study's defaults (1.4 and 8 in SOLIDWORKS 2025). Printed so that a run is fully described.
        try { L(string.Format(IC, "mesh: element growth ratio {0}, minimum elements in a circle {1}", mesh.GrowthRatio, mesh.MinElementsInCircle)); } catch { }
        int re = st.RunAnalysis();
        if (re != 0) throw new Exception("the solve failed (error " + re + "). Usual cause: the supports leave the shaft free to move.");

        CWResults res = st.Results;
        int err;
        double[] nd = Arr(mesh.GetNodes());
        int per = nd.Length / r.Nodes;
        double[] dp = Arr(res.GetTranslationalDisplacement(1, null, (int)swsLinearUnit_e.swsLinearUnitMillimeters, out err));
        int dper = dp.Length / r.Nodes;
        double[] ss = Arr(res.GetStress(0, 1, null, (int)swsStrengthUnit_e.swsStrengthUnitNewtonPerSquareMillimeter, out err));
        int sper = ss.Length / r.Nodes;
        if (per < 4 || dper < 5 || sper < 11) throw new Exception("unexpected result arrays (" + per + ", " + dper + ", " + sper + " values per node)");
        var X = new Dictionary<int, double[]>(); var U = new Dictionary<int, double[]>(); var V = new Dictionary<int, double>(); var SA = new Dictionary<int, double>();
        for (int i = 0; i < r.Nodes; i++) X[(int)nd[i * per]] = new[] { nd[i * per + 1] / MM, nd[i * per + 2] / MM, nd[i * per + 3] / MM };
        for (int i = 0; i < dp.Length / dper; i++) U[(int)dp[i * dper]] = new[] { dp[i * dper + 1], dp[i * dper + 2], dp[i * dper + 3], dp[i * dper + 4] };
        for (int i = 0; i < ss.Length / sper; i++) { V[(int)ss[i * sper]] = ss[i * sper + 10]; SA[(int)ss[i * sper]] = ss[i * sper + 1 + g.K]; }   // von Mises; normal stress along the axis

        r.UMax = -1; r.VMax = -1;
        foreach (var kv in U) if (kv.Value[3] > r.UMax && X.ContainsKey(kv.Key)) { r.UMax = kv.Value[3]; r.UMaxA = X[kv.Key][g.K]; r.UMaxR = g.Radial(X[kv.Key]); }
        foreach (var kv in V) if (kv.Value > r.VMax && X.ContainsKey(kv.Key)) { r.VMax = kv.Value; r.VMaxP = X[kv.Key]; r.VMaxA = r.VMaxP[g.K]; r.VMaxR = g.Radial(r.VMaxP); }

        int t1 = (g.K + 1) % 3, t2 = (g.K + 2) % 3;
        foreach (Station s in x.Stations)
        {
            var sr = new StRes { S = s, R = g.Rout(s.A) };
            if (double.IsNaN(sr.R)) { r.St.Add(sr); continue; }
            // the ring of surface nodes on the split circle; if the station has none (it sat on an existing edge), a thin slice
            double tol = 1e-3;
            for (int pass = 0; pass < 2 && sr.Nodes == 0; pass++)
            {
                double sy = 0, sz = 0, s1 = 0, syy = 0, szz = 0, syz = 0, sys = 0, szs = 0, s1s = 0;
                double[] u = { 0, 0, 0 };
                foreach (var kv in X)
                {
                    double[] p = kv.Value;
                    if (Math.Abs(p[g.K] - s.A) > tol) continue;
                    double rr = g.Radial(p);
                    if (rr < 0.97 * sr.R || rr > sr.R + 0.05) continue;
                    double[] uu = U[kv.Key];
                    for (int k = 0; k < 3; k++) u[k] += uu[k];
                    if (V[kv.Key] > sr.Vm) sr.Vm = V[kv.Key];
                    double y = p[t1] - g.C[t1], z = p[t2] - g.C[t2], sg = SA[kv.Key];
                    sy += y; sz += z; s1 += 1; syy += y * y; szz += z * z; syz += y * z; sys += y * sg; szs += z * sg; s1s += sg;
                    sr.Nodes++;
                }
                if (sr.Nodes == 0) { tol = 0.4 * minMm; sr.Slice = true; sr.Vm = -1; continue; }
                for (int k = 0; k < 3; k++) sr.U[k] = u[k] / sr.Nodes;
                sr.Ut = Math.Sqrt(sr.U[t1] * sr.U[t1] + sr.U[t2] * sr.U[t2]);
                // bending moment carried by the section, from a plane fitted through the axial stress on the ring:
                // stress = a*y + b*z + c, M = I * sqrt(a^2 + b^2). Only meaningful where the section is a full circle (or ring).
                bool round = !sr.Slice && sr.Nodes >= 6 && g.Cyls.Any(cy => !cy.Inner && cy.Full && Math.Abs(cy.R - sr.R) < 1e-4 && s.A >= cy.A0 - 1e-4 && s.A <= cy.A1 + 1e-4);
                if (round)
                {
                    double[,] A = { { syy, syz, sy }, { syz, szz, sz }, { sy, sz, s1 } };
                    double[] B = { sys, szs, s1s };
                    double det = A[0, 0] * (A[1, 1] * A[2, 2] - A[1, 2] * A[2, 1]) - A[0, 1] * (A[1, 0] * A[2, 2] - A[1, 2] * A[2, 0]) + A[0, 2] * (A[1, 0] * A[2, 1] - A[1, 1] * A[2, 0]);
                    if (Math.Abs(det) > 1e-12)
                    {
                        double da = B[0] * (A[1, 1] * A[2, 2] - A[1, 2] * A[2, 1]) - A[0, 1] * (B[1] * A[2, 2] - A[1, 2] * B[2]) + A[0, 2] * (B[1] * A[2, 1] - A[1, 1] * B[2]);
                        double db = A[0, 0] * (B[1] * A[2, 2] - A[1, 2] * B[2]) - B[0] * (A[1, 0] * A[2, 2] - A[1, 2] * A[2, 0]) + A[0, 2] * (A[1, 0] * B[2] - B[1] * A[2, 0]);
                        double ri = g.Rin(s.A);
                        double I = Math.PI * (Math.Pow(2 * sr.R, 4) - Math.Pow(2 * ri, 4)) / 64;
                        sr.Moment = I * Math.Sqrt(da * da + db * db) / Math.Abs(det);
                    }
                }
            }
            r.St.Add(sr);
        }
        r.Seconds = clock.Elapsed.TotalSeconds;
        return r;
    }

    static StRes At(Res r, double a) { return r.St.FirstOrDefault(s => Math.Abs(s.S.A - a) < 0.02 && s.Nodes > 0); }

    static void Report(Ctx x, Res r)
    {
        Geo g = x.G;
        L("");
        L("==== results: " + x.Name + " / " + x.Study + " ====");
        L(string.Format(IC, "largest displacement   {0:F5} mm at s = {1:F2}, r = {2:F2}", r.UMax, r.UMaxA - g.Off, r.UMaxR));
        L(string.Format(IC, "largest von Mises      {0:F4} MPa at s = {1:F2}, r = {2:F2}", r.VMax, r.VMaxA - g.Off, r.VMaxR));
        L(string.Format(IC, "factor of safety       {0:F2}  (yield {1:F1} MPa / largest von Mises)", x.Yield / r.VMax, x.Yield));
        L("sections (u = displacement of the section centre; uT = its part across the axis; vM = largest von Mises on the surface ring;");
        L("          M = bending moment fitted from the axial stress, where the section is a full circle):");
        L("        s   dia     uT mm      u" + AX[0] + " mm      u" + AX[1] + " mm      u" + AX[2] + " mm   vM MPa    M N.mm  what");
        foreach (StRes s in r.St)
        {
            if (s.Nodes == 0) { L(string.Format(IC, "  {0,7:F2}  (no surface nodes here: not on a cylindrical part of the shaft)  {1}", s.S.A - g.Off, s.S.Kind)); continue; }
            L(string.Format(IC, "  {0,7:F2} {1,5:F1} {2,9:F5} {3,10:F5} {4,10:F5} {5,10:F5} {6,8:F3} {7,9} {8}{9}", s.S.A - g.Off, 2 * s.R, s.Ut, s.U[0], s.U[1], s.U[2], s.Vm,
                double.IsNaN(s.Moment) ? "-" : N(s.Moment, "F1"), s.S.Kind, s.Slice ? " (thin slice, not a ring)" : ""));
        }
        foreach (Support s in x.Supports)
        {
            StRes a = At(r, s.A0), b = At(r, s.A1);
            if (a == null || b == null) continue;
            double d = 0; for (int k = 0; k < 3; k++) if (k != g.K) d += (b.U[k] - a.U[k]) * (b.U[k] - a.U[k]);
            double slope = Math.Sqrt(d) / (s.A1 - s.A0);
            L(string.Format(IC, "slope of the shaft through \"{0}\": {1:F5} rad = {2:F1} arcmin", s.Name, slope, slope * 180 / Math.PI * 60));
        }
    }

    static Dictionary<string, object> ToJson(Ctx x, Res r)
    {
        Geo g = x.G;
        Func<double, object> nn = v => double.IsNaN(v) || double.IsInfinity(v) ? null : (object)Math.Round(v, 6);
        var d = new Dictionary<string, object>();
        d["case"] = x.Name; d["study"] = x.Study; d["cad"] = x.Cad; d["workPart"] = x.WorkPart; d["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", IC);
        d["axis"] = AX[g.K]; d["sOffset"] = g.Off; d["material"] = x.Material; d["yield_MPa"] = x.Yield;
        d["mesh"] = new Dictionary<string, object> { { "max_mm", nn(r.MaxSize) }, { "min_mm", nn(r.MinSize) }, { "default_max_mm", nn(r.DefMax) }, { "default_min_mm", nn(r.DefMin) }, { "elements", r.Elements }, { "nodes", r.Nodes }, { "worstJacobian", nn(r.WorstJac) }, { "maxAspectRatio", nn(r.MaxAR) } };
        d["max_displacement_mm"] = nn(r.UMax); d["max_displacement_s"] = nn(r.UMaxA - g.Off); d["max_displacement_r"] = nn(r.UMaxR);
        d["max_von_mises_MPa"] = nn(r.VMax); d["max_von_mises_s"] = nn(r.VMaxA - g.Off); d["max_von_mises_r"] = nn(r.VMaxR);
        d["factor_of_safety"] = nn(x.Yield / r.VMax);
        d["sections"] = r.St.Where(s => s.Nodes > 0).Select(s => new Dictionary<string, object> { { "s", nn(s.S.A - g.Off) }, { "what", s.S.Kind }, { "diameter", nn(2 * s.R) }, { "u_transverse_mm", nn(s.Ut) }, { "u_mm", s.U.Select(v => nn(v)).ToArray() }, { "von_mises_MPa", nn(s.Vm) }, { "moment_Nmm", nn(s.Moment) }, { "nodes", s.Nodes }, { "slice", s.Slice } }).ToArray();
        var sl = new List<object>();
        foreach (Support s in x.Supports)
        {
            StRes a = At(r, s.A0), b = At(r, s.A1);
            if (a == null || b == null) continue;
            double q = 0; for (int k = 0; k < 3; k++) if (k != g.K) q += (b.U[k] - a.U[k]) * (b.U[k] - a.U[k]);
            sl.Add(new Dictionary<string, object> { { "name", s.Name }, { "slope_rad", nn(Math.Sqrt(q) / (s.A1 - s.A0)) }, { "slope_arcmin", nn(Math.Sqrt(q) / (s.A1 - s.A0) * 180 / Math.PI * 60) } });
        }
        d["supports"] = sl.ToArray();
        d["warnings"] = Warnings.ToArray();
        return d;
    }

    static void MeshSpec(Dictionary<string, object> c, out double max, out double min, out double factor)
    {
        var md = Sub(c, "mesh");
        max = Num(md, "max_mm", 0); min = Num(md, "min_mm", 0); factor = Num(md, "factor", 0.5);
    }

    public static void Solve(string caseFile)
    {
        Ctx x = Build(caseFile);
        double max, min, factor;
        MeshSpec(x.Case, out max, out min, out factor);
        Res r = MeshRun(x, max, min, factor);
        Report(x, r);
        string js = Path.Combine(x.WorkDir, x.Name + " results.json");
        File.WriteAllText(js, new JavaScriptSerializer().Serialize(ToJson(x, r)));
        if (Flag(x.Case, "plots", true)) Plots(x);
        Save(x.M);
        L("results: " + js);
        L(string.Format(IC, "done in {0:F0} s", r.Seconds));
    }

    public static void Sweep(string caseFile, string factors)
    {
        Ctx x = Build(caseFile);
        if (factors == null) factors = "1,0.75,0.5";
        string csv = Path.Combine(x.WorkDir, x.Name + " mesh study.csv");
        var rows = new List<Res>(); var fs = new List<string>();
        var lines = new List<string> { "factor,max_mm,min_mm,elements,nodes,u_max_mm,vm_max_MPa," + string.Join(",", x.Stations.Select(s => "uT@" + N(s.A - x.G.Off, "0.###") + ",vM@" + N(s.A - x.G.Off, "0.###"))) };
        foreach (string f in factors.Split(','))
        {
            double fv = double.Parse(f, IC);
            L(""); L("---- mesh factor " + f);
            Res r = MeshRun(x, 0, 0, fv);
            rows.Add(r); fs.Add(f.Trim());
            lines.Add(string.Join(",", new[] { f.Trim(), N(r.MaxSize, "F4"), N(r.MinSize, "F4"), r.Elements.ToString(IC), r.Nodes.ToString(IC), N(r.UMax, "F5"), N(r.VMax, "F4") }.Concat(r.St.SelectMany(s => new[] { s.Nodes > 0 ? N(s.Ut, "F5") : "", s.Nodes > 0 ? N(s.Vm, "F4") : "" }))));
        }
        File.WriteAllLines(csv, lines);
        Res last = rows[rows.Count - 1];
        L(""); L("==== mesh study: change against the last (finest) run ====");
        L("  factor  elements   u max mm      %   vM max MPa      %   largest change of a section uT %");
        for (int i = 0; i < rows.Count; i++)
        {
            Res r = rows[i];
            double worst = 0;
            for (int k = 0; k < r.St.Count; k++) if (r.St[k].Nodes > 0 && last.St[k].Nodes > 0 && last.St[k].Ut > 1e-4 * last.UMax) worst = Math.Max(worst, Math.Abs(r.St[k].Ut / last.St[k].Ut - 1) * 100);
            L(string.Format(IC, "  {0,6} {1,9} {2,10:F5} {3,6:+0.00;-0.00;0.00} {4,12:F4} {5,6:+0.00;-0.00;0.00} {6,10:F2}", fs[i], r.Elements, r.UMax, (r.UMax / last.UMax - 1) * 100, r.VMax, (r.VMax / last.VMax - 1) * 100, worst));
        }
        L("A peak at a keyway end, groove or shoulder is a stress concentration: it keeps rising as the mesh gets finer, so judge");
        L("convergence by the displacements and by stresses away from such corners.");
        Report(x, last);
        File.WriteAllText(Path.Combine(x.WorkDir, x.Name + " results.json"), new JavaScriptSerializer().Serialize(ToJson(x, last)));
        Save(x.M);
        L("mesh study: " + csv);
    }

    // ---------- pictures ----------
    static void View(Ctx x, ModelDoc2 m, bool side)
    {
        Geo g = x.G;
        if (!side) { m.ShowNamedView2("*Isometric", -1); m.ViewZoomtofit2(); return; }
        // side view: shaft axis left to right, "up" from the case (default: a model axis across the shaft)
        double[] right = { 0, 0, 0 }; right[g.K] = 1;
        double[] up = Has(x.Case, "up") ? Vec3(x.Case, "up") : new double[] { 0, 0, 0 };
        if (!Has(x.Case, "up")) up[g.K == 1 ? 2 : 1] = 1;
        double dot = up[0] * right[0] + up[1] * right[1] + up[2] * right[2];
        for (int i = 0; i < 3; i++) up[i] -= dot * right[i];
        double len = Math.Sqrt(up[0] * up[0] + up[1] * up[1] + up[2] * up[2]);
        if (len < 1e-9) throw new Exception("\"up\" must not be parallel to the shaft axis");
        for (int i = 0; i < 3; i++) up[i] /= len;
        double[] o = { right[1] * up[2] - right[2] * up[1], right[2] * up[0] - right[0] * up[2], right[0] * up[1] - right[1] * up[0] };
        // columns of the 3 x 3 block are the screen axes in model coordinates: right, up, towards the viewer
        double[] t = { right[0], up[0], o[0], right[1], up[1], o[1], right[2], up[2], o[2], 0, 0, 0, 1, 0, 0, 0 };
        ((ModelView)m.ActiveView).Orientation3 = (MathTransform)((MathUtility)x.Sw.GetMathUtility()).CreateTransform(t);
        m.GraphicsRedraw2(); m.ViewZoomtofit2();
    }

    static void Png(Ctx x, ModelDoc2 m, string label)
    {
        string path = Path.Combine(x.WorkDir, x.Name + " " + label + ".png");
        m.GraphicsRedraw2();
        int e = 0, w = 0;
        bool ok = m.Extension.SaveAs3(path, 0, (int)swSaveAsOptions_e.swSaveAsOptions_Silent | (int)swSaveAsOptions_e.swSaveAsOptions_Copy, null, null, ref e, ref w);
        L("  picture " + Path.GetFileName(path) + (ok ? "" : " FAILED (error " + e + ")"));
    }

    static void Plots(Ctx x)
    {
        ModelDoc2 m = x.M; CWResults res = x.St.Results; int err;
        L("pictures:");
        m.ClearSelection2(true);
        foreach (swUserPreferenceToggle_e tg in new[] { swUserPreferenceToggle_e.swDisplayPlanes, swUserPreferenceToggle_e.swDisplaySketches, swUserPreferenceToggle_e.swDisplayOrigins, swUserPreferenceToggle_e.swDisplayAxes })
            m.SetUserPreferenceToggle((int)tg, false);
        try
        {
            CWPlot pq = res.GetPlot("Quality1", out err);
            if (pq != null) { pq.ActivatePlot(); View(x, m, false); Png(x, m, "mesh"); }
        }
        catch (Exception ex) { L("  mesh picture skipped: " + ex.Message); }
        CWPlot ps = res.CreatePlot((int)swsPlotResultTypes_e.swsResultStress, (int)swsStressComponent_e.swsStressComponentVON, (int)swsStrengthUnit_e.swsStrengthUnitNewtonPerSquareMillimeter, false, out err);
        if (ps != null) { ps.ActivatePlot(); View(x, m, false); Png(x, m, "von Mises"); ps.ActivatePlot(); View(x, m, true); Png(x, m, "von Mises (side)"); }
        CWPlot pd = res.CreatePlot((int)swsPlotResultTypes_e.swsResultDisplacementOrAmplitude, (int)swsDisplacementComponent_e.swsDisplacementComponentURES, (int)swsLinearUnit_e.swsLinearUnitMillimeters, false, out err);
        if (pd != null)
        {
            // CreatePlot leaves the displacement legend in angstrom; setting the unit again (code 0) gives mm
            pd.SetComponentUnitAndValueByElem((int)swsDisplacementComponent_e.swsDisplacementComponentURES, 0, false);
            pd.ActivatePlot(); View(x, m, false); Png(x, m, "displacement"); pd.ActivatePlot(); View(x, m, true); Png(x, m, "displacement (side)");
        }
        CWPlot pf = res.CreatePlot((int)swsPlotResultTypes_e.swsResultFactorOfSafety, 0, 0, false, out err);
        if (pf != null) { pf.ActivatePlot(); View(x, m, false); Png(x, m, "FOS"); }
    }

    // ---------- self-test ----------
    // A plain steel bar, 20 mm diameter and 200 mm long, drawn along model Z and off the origin, on two bearings with a load in the
    // middle. The answer is known from beam theory, so this shows that the tool, SOLIDWORKS and the licence all work on this PC,
    // and it exercises an axis that is neither X nor through the origin.
    public static void SelfTest(string dir)
    {
        SldWorks sw = Sw();
        Directory.CreateDirectory(dir);
        string cad = Path.Combine(dir, "selftest bar.SLDPRT");
        CloseIfOpen(sw, cad);
        if (File.Exists(cad)) File.Delete(cad);
        const double D = 20, Len = 200, F = 1000, E = 210000, Nu = 0.28, Cx = 30, Cy = 10;
        ModelDoc2 m = (ModelDoc2)sw.NewDocument(sw.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart), 0, 0, 0);
        if (m == null) throw new Exception("no default part template");
        if (!DefaultPlane(m, 0).Select2(false, 0)) throw new Exception("cannot select the Front plane");
        m.SketchManager.InsertSketch(true);
        m.SketchManager.CreateCircleByRadius(Cx * MM, Cy * MM, 0, D / 2 * MM);
        m.SketchManager.InsertSketch(true);
        Feature f = m.FeatureManager.FeatureExtrusion3(true, false, false, 0, 0, Len * MM, 0, false, false, false, false, 0, 0, false, false, false, false, true, true, true, 0, 0, false);
        if (f == null) throw new Exception("the test bar could not be extruded");
        int e = 0, w = 0;
        if (!m.Extension.SaveAs3(cad, 0, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref e, ref w)) throw new Exception("cannot save " + cad);
        sw.CloseDoc(m.GetTitle());
        string cf = Path.Combine(dir, "selftest.json");
        var c = new Dictionary<string, object>();
        c["name"] = "selftest"; c["cad"] = cad; c["workDir"] = dir; c["origin"] = "min"; c["plots"] = false;
        c["material"] = new Dictionary<string, object> { { "name", "test steel" }, { "E_MPa", E }, { "nu", Nu }, { "density", 7850 }, { "yield_MPa", 350 } };
        c["supports"] = new object[] { new Dictionary<string, object> { { "name", "A" }, { "from", 0 }, { "to", 10 } }, new Dictionary<string, object> { { "name", "B" }, { "from", 190 }, { "to", 200 } } };
        c["loads"] = new object[] { new Dictionary<string, object> { { "name", "P" }, { "at", 100 }, { "from", 95 }, { "to", 105 }, { "force", new double[] { 0, -F, 0 } } } };
        c["probes"] = new double[] { 52.5 };
        c["mesh"] = new Dictionary<string, object> { { "factor", 0.5 } };
        File.WriteAllText(cf, new JavaScriptSerializer().Serialize(c));
        Ctx x = Build(cf);
        if (x.G.K != 2) throw new Exception("self-test: the axis was found as " + AX[x.G.K] + ", expected Z");
        Res r = MeshRun(x, 0, 0, 0.5);
        Report(x, r);
        Save(x.M);
        // simply supported between the bearing centres (span 190), plus shear deflection
        double span = 190, I = Math.PI * Math.Pow(D, 4) / 64, A = Math.PI * D * D / 4, G = E / (2 * (1 + Nu)), kappa = 6 * (1 + Nu) / (7 + 6 * Nu);
        double beam = F * Math.Pow(span, 3) / (48 * E * I) + F * span / (4 * kappa * G * A);
        double mQuarter = F / 2 * (52.5 - 5);
        StRes mid = At(r, x.G.Off + 100), q = At(r, x.G.Off + 52.5);
        if (mid == null || q == null) throw new Exception("self-test: no result at the probe sections");
        double du = (mid.Ut / beam - 1) * 100, dm = (q.Moment / mQuarter - 1) * 100;
        L("");
        L(string.Format(IC, "self-test: deflection under the load {0:F5} mm, beam theory {1:F5} mm ({2:+0.0;-0.0}%)", mid.Ut, beam, du));
        L(string.Format(IC, "self-test: bending moment at s = 52.5  {0:F1} N.mm, statics {1:F1} N.mm ({2:+0.0;-0.0}%)", q.Moment, mQuarter, dm));
        bool ok = Math.Abs(du) < 5 && Math.Abs(dm) < 3;
        L(ok ? "SELF-TEST PASSED" : "SELF-TEST FAILED: the differences should be within 5 % (deflection) and 3 % (moment)");
        if (!ok) throw new Exception("self-test failed");
    }

    public static int Main(string[] a)
    {
        try
        {
            string cmd = a.Length > 0 ? a[0].ToLowerInvariant() : "";
            if (cmd == "inspect" && a.Length >= 2) Inspect(Path.GetFullPath(a[1]), a.Length > 2 ? Path.GetFullPath(a[2]) : null);
            else if (cmd == "solve" && a.Length >= 2) Solve(a[1]);
            else if (cmd == "sweep" && a.Length >= 2) Sweep(a[1], a.Length > 2 ? a[2] : null);
            else if (cmd == "selftest" && a.Length >= 2) SelfTest(Path.GetFullPath(a[1]));
            else if (cmd == "export" && a.Length >= 3) Export(Path.GetFullPath(a[1]), Path.GetFullPath(a[2]));
            else { L("usage: ShaftFea.exe inspect <cad> [out.json] | solve <case.json> | sweep <case.json> [factors] | selftest <folder> | export <part> <target.step>"); return 2; }
            if (Warnings.Count > 0) L(Warnings.Count + " warning(s) above");
            return 0;
        }
        catch (Exception ex)
        {
            Exception e = ex.InnerException ?? ex;
            L("ERROR: " + e.Message);
            if (!(e is COMException) && e.GetType() != typeof(Exception)) L(e.StackTrace);
            return 1;
        }
    }
}
