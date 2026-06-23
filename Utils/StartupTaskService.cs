using System;
using System.Linq;
using System.Security.Principal;
using Microsoft.Win32.TaskScheduler;

namespace ZenStatesDebugTool
{
    // Wraps the Windows Task Scheduler entry ("RyzenSDT") that re-applies a saved profile at
    // logon. Extracted from SettingsForm so the scheduled-task concern lives in one place and
    // the task name is no longer a string literal scattered across the form.
    //
    // NOTE: this registers an elevated (Highest) logon task that runs the exe from its
    // current path. See docs/security-review-2026-06-22.md (SEC-02) — if the install folder
    // is user-writable this is a privilege-escalation vector. Behaviour is intentionally
    // unchanged here; hardening is tracked separately.
    public static class StartupTaskService
    {
        public const string TaskName = "RyzenSDT";

        public static bool Exists()
        {
            using (var taskService = new TaskService())
                return taskService.GetTask(TaskName) != null;
        }

        public static void Register(string executablePath, string profileName, int delaySeconds = 5)
        {
            using (var taskService = new TaskService())
            {
                TaskDefinition taskDefinition = taskService.NewTask();

                taskDefinition.RegistrationInfo.Description =
                    "Run Ryzen SMU Debug Tool on user logon to apply a CO/PBO profile. Automatically created by RyzenSDT. Remove manually or from the checkbox in PBO tab.";
                taskDefinition.Principal.UserId = WindowsIdentity.GetCurrent().Name;
                taskDefinition.Principal.RunLevel = TaskRunLevel.Highest;
                taskDefinition.Principal.LogonType = TaskLogonType.InteractiveToken;

                var logonTrigger = new LogonTrigger { Delay = TimeSpan.FromSeconds(delaySeconds) };
                taskDefinition.Triggers.Add(logonTrigger);

                taskDefinition.Actions.Add(new ExecAction(executablePath, $"--applyprofile \"{profileName}\""));

                taskService.RootFolder.RegisterTaskDefinition(TaskName, taskDefinition);
            }
        }

        public static void Remove()
        {
            using (var taskService = new TaskService())
                taskService.RootFolder.DeleteTask(TaskName, false);
        }

        // The profile name baked into the task's --applyprofile argument, or null if the task
        // (or that argument) isn't present.
        public static string GetProfileName()
        {
            using (var taskService = new TaskService())
            {
                Task task = taskService.GetTask(TaskName);
                var exec = task?.Definition.Actions.OfType<ExecAction>().FirstOrDefault();
                if (exec == null) return null;
                int idx = exec.Arguments.IndexOf("--applyprofile", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return null;
                string rest = exec.Arguments.Substring(idx + "--applyprofile".Length).Trim().Trim('"');
                return rest.Length > 0 ? rest : null;
            }
        }
    }
}
