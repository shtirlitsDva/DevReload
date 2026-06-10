using System;
using System.IO;
using System.Xml.Linq;

using RevitDevReload.Core;

using Xunit;

namespace RevitDevReload.Tests
{
    public class AddinManifestWriterTests : IDisposable
    {
        private readonly string _dir;

        public AddinManifestWriterTests()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "rdr-manifest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void Write_ProducesValidApplicationManifest()
        {
            string path = Path.Combine(_dir, "RevitDevReload.addin");

            AddinManifestWriter.Write(
                path,
                assemblyPath: @"X:\repo\bin\RevitDevReload.R24.dll",
                addInId: "cab77f49-ae73-4f4e-a014-39b717f0691b",
                fullClassName: "RevitDevReload.RevitDevReloadApp",
                name: "RevitDevReload",
                vendorId: "DVRL",
                vendorDescription: "DevReload");

            var doc = XDocument.Load(path);
            var addIn = doc.Root!.Element("AddIn")!;

            Assert.Equal("RevitAddIns", doc.Root.Name.LocalName);
            Assert.Equal("Application", addIn.Attribute("Type")!.Value);
            Assert.Equal(@"X:\repo\bin\RevitDevReload.R24.dll", addIn.Element("Assembly")!.Value);
            Assert.Equal("cab77f49-ae73-4f4e-a014-39b717f0691b", addIn.Element("AddInId")!.Value);
            Assert.Equal("RevitDevReload.RevitDevReloadApp", addIn.Element("FullClassName")!.Value);
            Assert.Equal("RevitDevReload", addIn.Element("Name")!.Value);
            Assert.Equal("DVRL", addIn.Element("VendorId")!.Value);
        }

        [Fact]
        public void Write_OverwritesExistingFile()
        {
            string path = Path.Combine(_dir, "RevitDevReload.addin");
            File.WriteAllText(path, "garbage");

            AddinManifestWriter.Write(
                path, @"X:\a.dll", "cab77f49-ae73-4f4e-a014-39b717f0691b",
                "A.B", "A", "DVRL", "d");

            var doc = XDocument.Load(path);
            Assert.Equal("RevitAddIns", doc.Root!.Name.LocalName);
        }
    }
}
