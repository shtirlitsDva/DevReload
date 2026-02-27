using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

// Suppress AutoCAD's auto-discovery of [CommandMethod] attributes.
// Without this, ExtensionLoader.ProcessAssembly registers commands via its
// own internal CommandClass.AddCommand — a separate registry that
// Utils.RemoveCommand cannot clean up, causing eDuplicateKey on reload.
// Pointing at an empty class means AutoCAD finds zero commands to register.
// DevReload's CommandRegistrar handles registration via Utils.AddCommand instead.
[assembly: CommandClass(typeof(Example.Plugin.NoAutoCommands))]

namespace Example.Plugin
{
    /// <summary>
    /// Empty marker class referenced by <c>[assembly: CommandClass]</c>.
    /// AutoCAD's <c>ExtensionLoader</c> only scans this type for commands
    /// and finds none, preventing the dual-registry conflict.
    /// </summary>
    internal class NoAutoCommands { }

    /// <summary>
    /// Example commands demonstrating hot-reloadable <c>[CommandMethod]</c>
    /// attributes in an isolated ALC. Both instance and static methods are supported.
    /// </summary>
    public class ExampleCommands
    {
        /// <summary>
        /// Example instance command that draws a line and writes a message.
        /// Change the coordinates or message text, run EXDEV, and verify the
        /// updated code executes without restarting AutoCAD.
        /// </summary>
        [CommandMethod("EXCMD")]
        public void DrawLine()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;
            var db = doc.Database;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var btr = (BlockTableRecord)tr.GetObject(
                    db.CurrentSpaceId, OpenMode.ForWrite);

                var line = new Line(
                    new Point3d(0, 0, 0),
                    new Point3d(100, 100, 0));

                btr.AppendEntity(line);
                tr.AddNewlyCreatedDBObject(line, true);
                tr.Commit();
            }

            ed.WriteMessage($"\nLine added from isolated ALC! Time: {DateTime.Now:HH:mm:ss}");
        }

        /// <summary>
        /// Example static command. Static methods are also supported by
        /// <see cref="DevReload.CommandRegistrar"/>.
        /// </summary>
        [CommandMethod("EXCMD2")]
        public static void StaticMessage()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            ed.WriteMessage($"\nStatic command from isolated ALC! Time: {DateTime.Now:HH:mm:ss}");
        }
    }
}
