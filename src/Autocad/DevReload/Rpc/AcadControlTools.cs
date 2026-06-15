using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

using Acad.Rpc.Core;

using Autodesk.AutoCAD.ApplicationServices;

namespace DevReload.Rpc
{
    /// <summary>
    /// In-process AutoCAD process and document control, served over this
    /// instance's named pipe (<c>acad-rpc-&lt;pid&gt;</c>). Each method runs on
    /// the AutoCAD main thread via the host's idle-pump dispatcher.
    /// </summary>
    [AcadRpcSurface(Group = "acad")]
    public static class AcadControlTools
    {
        // ── Commands ──────────────────────────────────────────────────────

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Run an AutoCAD command and block until it finishes. Tokens are split on whitespace/newlines (e.g. \"TWCIRCLE\" or \"._CIRCLE 0,0 5\").")]
        public static async Task<string> SendCommand(
            [Description("Command + arguments, whitespace/newline separated.")] string commandString)
        {
            object[] tokens = (commandString ?? string.Empty)
                .Split(new[] { '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Cast<object>()
                .ToArray();
            if (tokens.Length == 0) return "no command";

            // Commands execute in document/command context; awaiting blocks
            // the call until the command completes.
            var docs = Application.DocumentManager;
            await docs.ExecuteInCommandContextAsync(_ =>
            {
                var doc = Application.DocumentManager.MdiActiveDocument
                    ?? throw new InvalidOperationException("no active document");
                doc.Editor.Command(tokens);
                return Task.CompletedTask;
            }, null);
            return "ok";
        }

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Queue an AutoCAD command and return immediately. Use the raw command string with terminators (e.g. \"._LINE\\n0,0\\n10,10\\n\\n\").")]
        public static string PostCommand(
            [Description("Raw command string including terminators.")] string commandString)
        {
            var doc = Application.DocumentManager.MdiActiveDocument
                ?? throw new InvalidOperationException("no active document");
            doc.SendStringToExecute(commandString ?? string.Empty, true, false, false);
            return "queued";
        }

        // ── State ─────────────────────────────────────────────────────────

        [AcadRpcTool, RunOnAcadMainThread,
         Description("State snapshot: quiescent, active document name, open-document count.")]
        public static AcadLiveState GetState()
        {
            var docs = Application.DocumentManager;
            var doc = docs.MdiActiveDocument;
            return new AcadLiveState(
                IsQuiescent: true,
                HasActiveDocument: doc != null,
                ActiveDocumentName: doc?.Name ?? string.Empty,
                DocumentCount: docs.Count);
        }

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Return once the instance is quiescent. For cold-start readiness gate on acad_wait_pipe instead.")]
        public static AcadLiveState WaitQuiescent() => GetState();

        // ── Documents ─────────────────────────────────────────────────────

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Open a drawing in this instance.")]
        public static string OpenDrawing(
            [Description("Absolute path to a .dwg/.dwt/.dws file.")] string path,
            [Description("Open read-only? Default false.")] bool readOnly = false)
        {
            Application.DocumentManager.Open(path, readOnly);
            return "opened";
        }

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Create a new empty drawing in this instance. Optional template path; empty uses the default template.")]
        public static string NewDrawing(
            [Description("Optional template path (.dwt). Empty uses the default.")] string? templatePath = null)
        {
            Application.DocumentManager.Add(templatePath ?? string.Empty);
            return "created";
        }

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Close the active drawing. saveChanges=false (default) discards unsaved changes.")]
        public static string CloseActiveDrawing(
            [Description("Save unsaved changes before closing? Default false.")] bool saveChanges = false)
        {
            var doc = Application.DocumentManager.MdiActiveDocument
                ?? throw new InvalidOperationException("no active document");
            if (saveChanges) doc.CloseAndSave(doc.Name);
            else doc.CloseAndDiscard();
            return "closed";
        }

        [AcadRpcTool, RunOnAcadMainThread,
         Description("List every open drawing in this instance, with name and active/read-only flags.")]
        public static IReadOnlyList<AcadDocumentEntry> ListOpenDocuments()
        {
            var docs = Application.DocumentManager;
            var active = docs.MdiActiveDocument;
            var result = new List<AcadDocumentEntry>();
            foreach (Document d in docs)
                result.Add(new AcadDocumentEntry(d.Name, d == active, d.IsReadOnly));
            return result;
        }

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Switch the active document by its name (as reported by acad_list_open_documents). Errors if no open document matches.")]
        public static string ActivateDocument(
            [Description("The drawing's name (full path, or the short name for an unsaved drawing).")] string documentName)
        {
            var docs = Application.DocumentManager;
            foreach (Document d in docs)
            {
                if (string.Equals(d.Name, documentName, StringComparison.OrdinalIgnoreCase))
                {
                    docs.MdiActiveDocument = d;
                    return "activated";
                }
            }
            throw new InvalidOperationException($"no open document named '{documentName}'");
        }
    }

    public sealed record AcadLiveState(
        bool IsQuiescent,
        bool HasActiveDocument,
        string ActiveDocumentName,
        int DocumentCount);

    public sealed record AcadDocumentEntry(
        string Name,
        bool IsActive,
        bool IsReadOnly);
}
