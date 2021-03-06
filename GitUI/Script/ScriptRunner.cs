﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using GitCommands;
using GitUIPluginInterfaces;

namespace GitUI.Script
{
    /// <summary>Runs scripts.</summary>
    public static class ScriptRunner
    {
        /// <summary>Tries to run scripts identified by a <paramref name="command"/></summary>
        public static CommandStatus ExecuteScriptCommand(IWin32Window owner, GitModule module, int command, IGitUICommands uiCommands, RevisionGridControl revisionGrid = null)
        {
            var anyScriptExecuted = false;
            var needsGridRefresh = false;

            foreach (var script in ScriptManager.GetScripts())
            {
                if (script.HotkeyCommandIdentifier == command)
                {
                    var result = RunScript(owner, module, script.Name, uiCommands, revisionGrid);
                    anyScriptExecuted = true;
                    needsGridRefresh |= result.NeedsGridRefresh;
                }
            }

            return new CommandStatus(anyScriptExecuted, needsGridRefresh);
        }

        public static CommandStatus RunScript(IWin32Window owner, GitModule module, string scriptKey, IGitUICommands uiCommands, RevisionGridControl revisionGrid)
        {
            if (string.IsNullOrEmpty(scriptKey))
            {
                return false;
            }

            var script = ScriptManager.GetScript(scriptKey);

            if (script == null)
            {
                MessageBox.Show(owner, "Cannot find script: " + scriptKey, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (string.IsNullOrEmpty(script.Command))
            {
                return false;
            }

            string argument = script.Arguments;
            foreach (string option in ScriptOptionsParser.Options)
            {
                if (string.IsNullOrEmpty(argument) || !argument.Contains(option))
                {
                    continue;
                }

                if (!option.StartsWith("s"))
                {
                    continue;
                }

                if (revisionGrid != null)
                {
                    continue;
                }

                MessageBox.Show(owner,
                    $"Option {option} is only supported when started from revision grid.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            return RunScript(owner, module, script, uiCommands, revisionGrid);
        }

        private static CommandStatus RunScript(IWin32Window owner, GitModule module, ScriptInfo scriptInfo, IGitUICommands uiCommands, RevisionGridControl revisionGrid)
        {
            if (scriptInfo.AskConfirmation && MessageBox.Show(owner, $"Do you want to execute '{scriptInfo.Name}'?", "Script", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No)
            {
                return false;
            }

            string originalCommand = scriptInfo.Command;
            (string argument, bool abort) = ScriptOptionsParser.Parse(scriptInfo.Arguments, module, owner, revisionGrid);
            if (abort)
            {
                return false;
            }

            string command = OverrideCommandWhenNecessary(originalCommand);
            command = ExpandCommandVariables(command, module);

            if (scriptInfo.IsPowerShell)
            {
                PowerShellHelper.RunPowerShell(command, argument, module.WorkingDir, scriptInfo.RunInBackground);
                return new CommandStatus(true, false);
            }

            if (command.StartsWith(PluginPrefix))
            {
                command = command.Replace(PluginPrefix, "");
                foreach (var plugin in PluginRegistry.Plugins)
                {
                    if (plugin.Description.ToLower().Equals(command, StringComparison.CurrentCultureIgnoreCase))
                    {
                        var eventArgs = new GitUIEventArgs(owner, uiCommands);
                        return new CommandStatus(true, plugin.Execute(eventArgs));
                    }
                }

                return false;
            }

            if (command.StartsWith(NavigateToPrefix))
            {
                if (revisionGrid == null)
                {
                    return false;
                }

                command = command.Replace(NavigateToPrefix, string.Empty);
                if (!command.IsNullOrEmpty())
                {
                    var revisionRef = new Executable(command, module.WorkingDir).GetOutputLines(argument).FirstOrDefault();

                    if (revisionRef != null)
                    {
                        revisionGrid.GoToRef(revisionRef, true);
                    }
                }

                return new CommandStatus(true, false);
            }

            if (!scriptInfo.RunInBackground)
            {
                FormProcess.ShowStandardProcessDialog(owner, command, argument, module.WorkingDir, null, true);
            }
            else
            {
                if (originalCommand.Equals("{openurl}", StringComparison.CurrentCultureIgnoreCase))
                {
                    Process.Start(argument);
                }
                else
                {
                    new Executable(command, module.WorkingDir).Start(argument);
                }
            }

            return new CommandStatus(true, !scriptInfo.RunInBackground);
        }

        private static string ExpandCommandVariables(string originalCommand, GitModule module)
        {
            return originalCommand.Replace("{WorkingDir}", module.WorkingDir);
        }

        private const string PluginPrefix = "plugin:";
        private const string NavigateToPrefix = "navigateTo:";

        private static string OverrideCommandWhenNecessary(string originalCommand)
        {
            // Make sure we are able to run git, even if git is not in the path
            if (originalCommand.Equals("git", StringComparison.CurrentCultureIgnoreCase) ||
                originalCommand.Equals("{git}", StringComparison.CurrentCultureIgnoreCase))
            {
                return AppSettings.GitCommand;
            }

            if (originalCommand.Equals("gitextensions", StringComparison.CurrentCultureIgnoreCase) ||
                originalCommand.Equals("{gitextensions}", StringComparison.CurrentCultureIgnoreCase) ||
                originalCommand.Equals("gitex", StringComparison.CurrentCultureIgnoreCase) ||
                originalCommand.Equals("{gitex}", StringComparison.CurrentCultureIgnoreCase))
            {
                return AppSettings.GetGitExtensionsFullPath();
            }

            if (originalCommand.Equals("{openurl}", StringComparison.CurrentCultureIgnoreCase))
            {
                return "explorer";
            }

            // Prefix should be {plugin:pluginname},{plugin=pluginname}
            var match = System.Text.RegularExpressions.Regex.Match(originalCommand, @"\{plugin.(.+)\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && match.Groups.Count > 1)
            {
                originalCommand = $"{PluginPrefix}{match.Groups[1].Value.ToLower()}";
            }

            return originalCommand;
        }
    }
}