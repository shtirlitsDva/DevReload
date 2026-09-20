using System;
using System.Collections.Generic;

using Acad.Rpc.Core;

using Autodesk.AutoCAD.ApplicationServices;

namespace DevReload.Rpc
{
    /// <summary>
    /// This host's implementation of the state keys its tools declare with
    /// <see cref="RpcRequiresAttribute"/>. The engine enforces; this file says
    /// what the keys mean in AutoCAD.
    /// </summary>
    /// <remarks>
    /// One check, one message, one place. Before this, five tools each carried
    /// their own <c>?? throw new InvalidOperationException("no active document")</c>
    /// and two more carried nothing at all, so the same missing document produced
    /// three different outcomes depending on which tool the agent reached for.
    ///
    /// <para>The AutoCAD Start tab is the case this keeps being needed for: it
    /// holds focus with <c>MdiActiveDocument</c> null, so "AutoCAD is open" and
    /// "there is a drawing to act on" are not the same question.</para>
    /// </remarks>
    internal static class AcadStateChecks
    {
        /// <summary>A drawing is current. Named here, declared by tools as
        /// <c>[RpcRequires(AcadStateChecks.Document)]</c>.</summary>
        internal const string Document = "document";

        internal static IReadOnlyDictionary<string, RpcStateCheck> All { get; } =
            new Dictionary<string, RpcStateCheck>(StringComparer.Ordinal)
            {
                [Document] = new RpcStateCheck(
                    Requirement: "current drawing",
                    Check: () => Application.DocumentManager.MdiActiveDocument != null
                        ? null
                        : "No current drawing. acad_list_open_documents lists open ones, " +
                          "acad_activate_document makes one current, acad_new_drawing and " +
                          "acad_open_drawing create one."),
            };
    }
}
