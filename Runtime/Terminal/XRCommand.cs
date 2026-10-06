using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.CCK.Language;
using Nox.Terminal;
using Nox.XR.Runtime.Diagnostics;
using Nox.XR.Runtime.FullBody;
using Nox.XR.Runtime.Operators;

namespace Nox.XR.Runtime.Terminal {
	/// <summary>
	/// <c>xr</c> terminal command: prints the same diagnostics as the <c>nox.control</c> operators
	/// (<see cref="XRControl"/>) and can drive the full-body calibration from the in-game terminal.
	/// </summary>
	public class XRCommand : ICommand, IHelper {
		private static readonly string[] SubCommands = {
			"help", "state", "devices", "trackers", "fbt", "diagnose", "calibrate"
		};

		private static readonly string[] CalibrationActions = {
			"status", "start", "confirm", "cancel", "clear"
		};

		public string GetName()
			=> "xr";

		public string GetDescription()
			=> LanguageManager.Get($"terminal.command.{GetName()}.description");

		public string GetShort()
			=> LanguageManager.Get($"terminal.command.{GetName()}.short");

		public string GetUsage()
			=> LanguageManager.Get($"terminal.command.{GetName()}.usage");

		public string[] AutoComplete(string input, IContext context = null) {
			if (string.IsNullOrWhiteSpace(input))
				return Array.Empty<string>();

			var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (!GetName().StartsWith(parts[0], StringComparison.OrdinalIgnoreCase))
				return Array.Empty<string>();

			// "xr <subcommand>" first token after the command name.
			if (parts.Length <= 1)
				return SubCommands.Select(s => $"{GetName()} {s}").ToArray();

			if (parts.Length == 2 && !input.EndsWith(' ')) {
				return SubCommands
					.Where(s => s.StartsWith(parts[1], StringComparison.OrdinalIgnoreCase))
					.Select(s => $"{GetName()} {s}")
					.ToArray();
			}

			if (parts.Length <= 3 && parts[1].Equals("calibrate", StringComparison.OrdinalIgnoreCase))
				return CalibrationActions
					.Where(a => parts.Length < 3 || a.StartsWith(parts[2], StringComparison.OrdinalIgnoreCase))
					.Select(a => $"{GetName()} calibrate {a}")
					.ToArray();

			return Array.Empty<string>();
		}

		public UniTask<bool> Execute(string input, IContext context = null)
			=> UniTask.FromResult(ExecuteInternal(input, context));

		private bool ExecuteInternal(string input, IContext context) {
			if (string.IsNullOrWhiteSpace(input))
				return false;

			var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (!parts[0].Equals(GetName(), StringComparison.OrdinalIgnoreCase))
				return false;

			var printing = context?.CanPrinting() ?? false;
			var sub      = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "help";

			void Print(string line) {
				if (printing)
					context.PrintLn(line);
			}

			switch (sub) {
				case "help":
					Print(GetUsage());
					Print("  xr help                  — this help");
					Print("  xr state                 — loader, providers, headset/hands, settings");
					Print("  xr devices [--tracked]   — all XR devices + tracker-filter verdict");
					Print("  xr trackers [--all]      — trackers used by the game (--all: rejected ones too)");
					Print("  xr fbt [--rig]           — full-body calibration state (--rig: rig targets)");
					Print("  xr diagnose              — why a tracker is or is not used");
					Print("  xr calibrate <action>    — status | start | confirm | cancel | clear");
					break;

				case "state": {
					var state = XRDiagnostics.State();
					Print(state.Describe("  "));

					var hints = XRDiagnostics.Hints();
					if (hints.Length > 0) {
						Print("  hints:");
						foreach (var hint in hints)
							Print("    " + hint);
					}

					context?.SetResult(state);
					break;
				}

				case "devices": {
					var trackedOnly = parts.Contains("--tracked") || parts.Contains("-t");
					var devices     = XRDiagnostics.Devices(trackedOnly);

					Print($"  devices: {devices.Count}{(trackedOnly ? " (tracked only)" : string.Empty)}");
					foreach (var device in devices)
						Print("    " + device.Describe());

					context?.SetResult(devices);
					break;
				}

				case "trackers": {
					var includeAll = parts.Contains("--all") || parts.Contains("-a");
					var devices    = XRDiagnostics.Trackers(includeAll);
					var used       = devices.Where(d => d.TrackerCandidate).ToArray();

					Print($"  trackers used: {used.Length} (listed: {devices.Count})"
						+ (TrackersAllowControllerFlagged ? ", controller-flagged trackers allowed" : string.Empty));
					foreach (var device in devices)
						Print("    " + device.Describe());

					context?.SetResult(used);
					break;
				}

				case "fbt":
				case "fullbody": {
					var withRig  = parts.Contains("--rig") || parts.Contains("-r");
					var snapshot = XRDiagnostics.FullBody(withRig);

					Print(snapshot.Describe("  "));

					if (withRig && snapshot.RigBones is { Length: > 0 })
						foreach (var bone in snapshot.RigBones)
							Print($"    {bone.Bone}: {(bone.Active ? "active" : "inactive")} {bone.Path}");

					context?.SetResult(snapshot);
					break;
				}

				case "diagnose": {
					var hints = XRDiagnostics.Hints();
					Print($"  XR diagnosis ({hints.Length} hint(s)):");
					foreach (var hint in hints)
						Print("    " + hint);

					context?.SetResult(hints);
					break;
				}

				case "calibrate": {
					var calibration = FullBodyCalibration.Instance;
					if (calibration == null) {
						Print("  full-body calibration is unavailable: the XR proxy is not created.");
						context?.SetResult(false);
						return true;
					}

					var action = parts.Length >= 3 ? parts[2].ToLowerInvariant() : "status";
					switch (action) {
						case "start":   calibration.StartCalibration();   break;
						case "confirm":
						case "save":    calibration.ConfirmCalibration(); break;
						case "cancel":  calibration.CancelCalibration();  break;
						case "clear":
						case "reset":   calibration.ClearCalibration();   break;
						case "status":  break;
						default:
							Print($"  unknown action '{action}' (expected: {string.Join(", ", CalibrationActions)}).");
							context?.SetResult(false);
							return true;
					}

					var snapshot = XRDiagnostics.FullBody();
					Print(snapshot.Describe("  "));
					context?.SetResult(snapshot);
					break;
				}

				default:
					Print($"  unknown subcommand '{sub}' ({string.Join(", ", SubCommands)}).");
					break;
			}

			return true;
		}

		private static bool TrackersAllowControllerFlagged
			=> Settings.TrackersIncludeControllerSetting.Value;
	}
}
