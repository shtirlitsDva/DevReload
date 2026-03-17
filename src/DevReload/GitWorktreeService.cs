using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace DevReload
{
    public record WorktreeInfo(string Path, string Branch, bool IsMain);

    public static class GitWorktreeService
    {
        public static string? GetRepoRoot(string pathInRepo)
        {
            string? output = RunGit(pathInRepo, "rev-parse --show-toplevel");
            return output?.Trim().Replace('/', '\\');
        }

        public static string? GetCurrentBranch(string repoOrWorktreePath)
        {
            string? output = RunGit(repoOrWorktreePath, "rev-parse --abbrev-ref HEAD");
            return output?.Trim();
        }

        public static List<WorktreeInfo> ListWorktrees(string repoRoot)
        {
            var result = new List<WorktreeInfo>();
            string? output = RunGit(repoRoot, "worktree list --porcelain");
            if (output == null) return result;

            string? currentPath = null;
            string? currentBranch = null;
            bool isMain = true; // first entry is always the main worktree

            foreach (string rawLine in output.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');

                if (line.StartsWith("worktree "))
                {
                    // Flush previous entry
                    if (currentPath != null)
                    {
                        result.Add(new WorktreeInfo(
                            currentPath,
                            currentBranch ?? "(detached)",
                            isMain));
                        isMain = false;
                    }

                    currentPath = line.Substring("worktree ".Length).Trim();
                    currentBranch = null;
                }
                else if (line.StartsWith("branch "))
                {
                    string refName = line.Substring("branch ".Length).Trim();
                    // refs/heads/feature-x → feature-x
                    currentBranch = refName.StartsWith("refs/heads/")
                        ? refName.Substring("refs/heads/".Length)
                        : refName;
                }
                else if (line == "detached")
                {
                    currentBranch = "(detached)";
                }
            }

            // Flush last entry
            if (currentPath != null)
            {
                result.Add(new WorktreeInfo(
                    currentPath,
                    currentBranch ?? "(detached)",
                    isMain));
            }

            return result;
        }

        public static string RemapToWorktree(
            string csprojPath, string mainRepoRoot, string worktreePath)
        {
            string relativePath = Path.GetRelativePath(mainRepoRoot, csprojPath);
            return Path.GetFullPath(Path.Combine(worktreePath, relativePath));
        }

        private static string? RunGit(string workingDir, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"-C \"{workingDir}\" {arguments}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = Process.Start(psi);
                if (proc == null) return null;

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();

                return proc.ExitCode == 0 ? output : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
