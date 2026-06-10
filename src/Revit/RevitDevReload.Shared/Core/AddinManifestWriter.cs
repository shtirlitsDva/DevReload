using System.IO;
using System.Xml.Linq;

namespace RevitDevReload.Core
{
    // Writes the .addin manifest Revit reads from
    // %APPDATA%\Autodesk\Revit\Addins\{year}. Application type only — the
    // host add-in is the single entry point; plugin commands never get their
    // own manifests (they are loaded/invoked by the host).
    public static class AddinManifestWriter
    {
        public static void Write(
            string manifestPath,
            string assemblyPath,
            string addInId,
            string fullClassName,
            string name,
            string vendorId,
            string vendorDescription)
        {
            var doc = new XDocument(
                new XElement("RevitAddIns",
                    new XElement("AddIn",
                        new XAttribute("Type", "Application"),
                        new XElement("Name", name),
                        new XElement("Assembly", assemblyPath),
                        new XElement("AddInId", addInId),
                        new XElement("FullClassName", fullClassName),
                        new XElement("VendorId", vendorId),
                        new XElement("VendorDescription", vendorDescription))));

            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            doc.Save(manifestPath);
        }
    }
}
