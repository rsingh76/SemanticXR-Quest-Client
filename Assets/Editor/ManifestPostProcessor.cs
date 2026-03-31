#if UNITY_ANDROID
using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

public class ManifestPostProcessor : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 99;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        // Unity 6 may pass the unityLibrary path or the launcher path
        // We need to patch both possible manifest locations
        string[] possiblePaths = new[]
        {
            Path.Combine(path, "src", "main", "AndroidManifest.xml"),
            Path.Combine(path, "..", "launcher", "src", "main", "AndroidManifest.xml"),
            Path.Combine(path, "..", "unityLibrary", "src", "main", "AndroidManifest.xml"),
        };

        foreach (var manifestPath in possiblePaths)
        {
            if (File.Exists(manifestPath))
            {
                PatchManifest(manifestPath);
            }
        }

        // Force app_name to SemanticXR in the launcher strings.xml
        string[] stringsXmlPaths = new[]
        {
            Path.Combine(path, "src", "main", "res", "values", "strings.xml"),
            Path.Combine(path, "..", "launcher", "src", "main", "res", "values", "strings.xml"),
        };

        foreach (var stringsPath in stringsXmlPaths)
        {
            if (File.Exists(stringsPath))
            {
                PatchAppName(stringsPath, "SemanticXR");
            }
        }
    }

    static void PatchAppName(string stringsXmlPath, string appName)
    {
        var doc = new XmlDocument();
        doc.Load(stringsXmlPath);

        var nodes = doc.SelectNodes("//string[@name='app_name']");
        if (nodes != null && nodes.Count > 0)
        {
            foreach (XmlNode node in nodes)
            {
                if (node.InnerText != appName)
                {
                    node.InnerText = appName;
                    doc.Save(stringsXmlPath);
                    Debug.Log($"[ManifestPostProcessor] Patched app_name to '{appName}' in: {stringsXmlPath}");
                }
            }
        }
    }

    static void PatchManifest(string manifestPath)
    {
        var doc = new XmlDocument();
        doc.Load(manifestPath);

        var root = doc.DocumentElement;
        var nsMgr = new XmlNamespaceManager(doc.NameTable);
        nsMgr.AddNamespace("android", "http://schemas.android.com/apk/res/android");
        string ns = "http://schemas.android.com/apk/res/android";

        bool changed = false;
        changed |= AddPermission(doc, root, ns, "android.permission.INTERNET");
        changed |= AddPermission(doc, root, ns, "android.permission.ACCESS_NETWORK_STATE");
        changed |= AddFeature(doc, root, ns, "oculus.software.overlay_keyboard", false);

        // Add MR mode meta-data inside <application>
        var appNodes = root.GetElementsByTagName("application");
        if (appNodes.Count > 0)
        {
            var app = (XmlElement)appNodes[0];
            changed |= AddMetaData(doc, app, ns, "com.oculus.mr_mode", "true");
        }

        if (changed)
        {
            doc.Save(manifestPath);
            Debug.Log($"[ManifestPostProcessor] Patched: {manifestPath}");
        }
    }

    static bool AddPermission(XmlDocument doc, XmlElement root, string ns, string name)
    {
        foreach (XmlNode n in root.ChildNodes)
            if (n.Name == "uses-permission" && n.Attributes?.GetNamedItem("name", ns)?.Value == name)
                return false;
        var e = doc.CreateElement("uses-permission");
        e.SetAttribute("name", ns, name);
        root.AppendChild(e);
        return true;
    }

    static bool AddMetaData(XmlDocument doc, XmlElement parent, string ns, string name, string value)
    {
        foreach (XmlNode n in parent.ChildNodes)
            if (n.Name == "meta-data" && n.Attributes?.GetNamedItem("name", ns)?.Value == name)
                return false;
        var e = doc.CreateElement("meta-data");
        e.SetAttribute("name", ns, name);
        e.SetAttribute("value", ns, value);
        parent.AppendChild(e);
        return true;
    }

    static bool AddFeature(XmlDocument doc, XmlElement root, string ns, string name, bool req)
    {
        foreach (XmlNode n in root.ChildNodes)
            if (n.Name == "uses-feature" && n.Attributes?.GetNamedItem("name", ns)?.Value == name)
                return false;
        var e = doc.CreateElement("uses-feature");
        e.SetAttribute("name", ns, name);
        e.SetAttribute("required", ns, req ? "true" : "false");
        root.AppendChild(e);
        return true;
    }
}
#endif
