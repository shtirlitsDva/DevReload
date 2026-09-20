using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Acad.Rpc.Core;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Internal;

// IsQuiescent lives on the core Application, which the ApplicationServices one
// derives from. Named explicitly so the reader can find the API that answers it.
using CoreApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

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

        [AcadRpcTool, RunOnAcadMainThread, RpcRequires(AcadStateChecks.Document),
         Description("Run an AutoCAD command and block until it finishes. Tokens are split on whitespace/newlines (e.g. \"TWCIRCLE\" or \"._CIRCLE 0,0 5\").")]
        public static async Task<string> SendCommand(
            [Description("Command + arguments, whitespace/newline separated.")] string commandString)
        {
            object[] tokens = (commandString ?? string.Empty)
                .Split(new[] { '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Cast<object>()
                .ToArray();
            if (tokens.Length == 0) return "no command";

            // Editor.Command dispatches the first token straight into AutoCAD's
            // native command engine. An UNREGISTERED command name faults in
            // native code and kills the whole process — reproduced by sending a
            // command that does not exist (e.g. ACDMCP_START before Acd.Mcp is
            // loaded). The command-line interpreter PostCommand uses rejects
            // unknown input harmlessly; Editor.Command does not. So verify the
            // command is defined first, and fail with a clean error instead.
            // (A try/catch cannot help here: the fault is process-fatal, not a
            // managed exception.) Leading '.'/'_'/'\'' are command modifiers, not
            // part of the name; strip them before the lookup. '-' is kept — the
            // dash form (e.g. -LAYER) is a distinct registered command.
            string firstToken = (string)tokens[0];
            string commandName = firstToken.TrimStart('.', '_', '\'');
            if (commandName.Length == 0 || !Utils.IsCommandDefined(commandName))
                throw new InvalidOperationException(
                    $"unknown AutoCAD command '{firstToken}': not defined in this instance — nothing was run. " +
                    "If it belongs to a plugin, load the plugin first (e.g. devreload_load_plugin).");

            // Commands execute in document/command context; awaiting blocks
            // the call until the command completes.
            var docs = Application.DocumentManager;
            await docs.ExecuteInCommandContextAsync(_ =>
            {
                // The precondition covered entry; this covers the switch into
                // command context, which is a later moment and can find the
                // document gone.
                var doc = Application.DocumentManager.MdiActiveDocument
                    ?? throw new InvalidOperationException(
                        "the current drawing closed while switching to command context");
                doc.Editor.Command(tokens);
                return Task.CompletedTask;
            }, null);
            return "ok";
        }

        [AcadRpcTool, RunOnAcadMainThread, RpcRequires(AcadStateChecks.Document),
         Description("Queue an AutoCAD command and return immediately. Use the raw command string with terminators (e.g. \"._LINE\\n0,0\\n10,10\\n\\n\"). Runs in application context and queues into the current drawing's command queue; \"queued\" means accepted, not executed.")]
        public static string PostCommand(
            [Description("Raw command string including terminators.")] string commandString)
        {
            var doc = Application.DocumentManager.MdiActiveDocument!;
            doc.SendStringToExecute(commandString ?? string.Empty, true, false, false);
            return "queued";
        }

        // ── State ─────────────────────────────────────────────────────────

        [AcadRpcTool, RunOnAcadMainThread,
         Description("State snapshot: quiescence, command in progress, execution context, documents. Read from application context, where isQuiescent false means AutoCAD is busy with something else.")]
        public static AcadLiveState GetState() => Snapshot();

        /// <remarks>
        /// isQuiescent used to be the literal <c>true</c>, so a caller could not
        /// learn anything from it. These are the dedicated APIs for each fact,
        /// nothing inferred from anything else:
        /// <c>Core.Application.IsQuiescent</c> is the managed twin of COM's
        /// <c>GetAcadState().IsQuiescent</c>.
        ///
        /// <para>Thread and context matter: the quiescence APIs are main-thread
        /// only, and asked from inside a command they always answer false —
        /// measured, not assumed. Every caller here arrives through the idle pump
        /// in application context, which is the one place the answer is about
        /// AutoCAD rather than about the caller.</para>
        /// </remarks>
        private static AcadLiveState Snapshot()
        {
            var docs = Application.DocumentManager;
            var doc = docs.MdiActiveDocument;
            return new AcadLiveState(
                IsQuiescent: CoreApplication.IsQuiescent,
                ActiveCommand: doc?.CommandInProgress ?? string.Empty,
                IsApplicationContext: docs.IsApplicationContext,
                HasActiveDocument: doc != null,
                ActiveDocumentName: doc?.Name ?? string.Empty,
                DocumentCount: docs.Count);
        }

        // NOT RunOnAcadMainThread, unlike every other tool here: waiting for the
        // main thread to go idle while holding it is a deadlock. This runs off it
        // and probes across, so AutoCAD is free to finish whatever it is doing.
        [AcadRpcTool,
         Description("Wait until AutoCAD is quiescent, then return the state snapshot. Throws on timeout. For cold-start readiness use acad_wait_pipe.")]
        public static async Task<AcadLiveState> WaitQuiescent(
            [Description("Milliseconds to wait. Default 30000.")] int timeoutMs = 30000)
        {
            var dispatcher = AcadRpcHost.Current.Dispatcher;
            long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);

            while (true)
            {
                int remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
                using var cts = new CancellationTokenSource(remaining);
                AcadLiveState state;
                try
                {
                    state = await dispatcher.InvokeAsync(Snapshot, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // The probe itself could not be served, which means the main
                    // thread never came free inside the budget.
                    throw new TimeoutException(
                        $"AutoCAD was still busy after {timeoutMs} ms.");
                }

                if (state.IsQuiescent) return state;
                if (Environment.TickCount64 >= deadline)
                    throw new TimeoutException(
                        $"AutoCAD was still busy after {timeoutMs} ms" +
                        (state.ActiveCommand.Length > 0
                            ? $" (running {state.ActiveCommand})." : "."));

                await Task.Delay(QuiescentProbeMs);
            }
        }

        private const int QuiescentProbeMs = 100;

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

        [AcadRpcTool, RunOnAcadMainThread, RpcRequires(AcadStateChecks.Document),
         Description("Close the active drawing. saveChanges=false (default) discards unsaved changes.")]
        public static string CloseActiveDrawing(
            [Description("Save unsaved changes before closing? Default false.")] bool saveChanges = false)
        {
            var doc = Application.DocumentManager.MdiActiveDocument!;
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

    /// <param name="IsQuiescent">AutoCAD is idle — not running a command, not
    /// waiting for input.</param>
    /// <param name="ActiveCommand">The command AutoCAD is running, empty when
    /// none.</param>
    /// <param name="IsApplicationContext">The context this snapshot was taken in.
    /// False means it was taken from inside a command, and isQuiescent is then
    /// false by construction.</param>
    public sealed record AcadLiveState(
        bool IsQuiescent,
        string ActiveCommand,
        bool IsApplicationContext,
        bool HasActiveDocument,
        string ActiveDocumentName,
        int DocumentCount);

    public sealed record AcadDocumentEntry(
        string Name,
        bool IsActive,
        bool IsReadOnly);
}
