using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.CCK.Control;
using Nox.Control;
using Nox.XR.Runtime.Diagnostics;
using Nox.XR.Runtime.FullBody;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Runtime.Operators {
	/// <summary>
	/// The operators <c>nox.xr</c> exposes to <c>nox.control</c> (MCP, REST <c>/api</c> and the
	/// WebSocket transport).
	/// <para>
	/// They are all read-only except <see cref="XRCalibrateOperator"/>, which drives the full-body
	/// calibration, so an external client can answer "why is my tracker not used?" and run a
	/// calibration without touching the headset.
	/// </para>
	/// </summary>
	public static class XRControl {
		/// <summary>Permission required to read the XR state.</summary>
		public const string ReadPermission = "xr:read";

		/// <summary>Permission required to drive the full-body calibration.</summary>
		public const string ControlPermission = "xr:control";

		/// <summary>Every operator provided by this mod, in registration order.</summary>
		public static IOperator[] All() => new IOperator[] {
			new XRStateOperator(),
			new XRDevicesOperator(),
			new XRTrackersOperator(),
			new XRFullBodyOperator(),
			new XRDiagnoseOperator(),
			new XRCalibrateOperator()
		};
	}

	/// <summary>Base class: main-thread marshalling and shared argument helpers.</summary>
	public abstract class XROperatorBase : IOperator {
		public abstract string Name { get; }
		public abstract string Description { get; }
		public virtual string[] RequiredPermissions => new[] { XRControl.ReadPermission };
		public virtual ISchema Schema => new InputSchema();

		public async UniTask<IOutput> Execute(IInput args) {
			// Control requests arrive on a pool thread (EmbedIO) while every value read here comes
			// from the Unity API, which is main-thread only.
			await UniTask.SwitchToMainThread();

			try {
				return await Run(args);
			} catch (Exception e) {
				Logger.LogError($"{Name} failed: {e.Message}", tag: nameof(XRControl));
				return OperatorOutput.Error($"{Name} failed: {e.Message}");
			}
		}

		protected abstract UniTask<IOutput> Run(IInput args);
	}

	/// <summary>Loader, provider, device counts and the FBT settings.</summary>
	public sealed class XRStateOperator : XROperatorBase {
		public override string Name => "xr_state";

		public override string Description
			=> "XR loader/provider state: active loader, input provider, headset/hands, device counts, origin, avatar scale and the FBT settings.";

		public override ISchema Schema => new InputSchema()
			.Property<bool>("with_hints", "Include the diagnosis hints (same as xr_diagnose).");

		protected override UniTask<IOutput> Run(IInput args) {
			var state = XRDiagnostics.State();

			return UniTask.FromResult<IOutput>(OperatorOutput.Ok(new {
				state,
				summary = state.Describe(string.Empty),
				hints   = args.Get<bool>("with_hints") ? XRDiagnostics.Hints() : null
			}));
		}
	}

	/// <summary>Raw device dump — the exact answer to "does Unity even see my tracker?".</summary>
	public sealed class XRDevicesOperator : XROperatorBase {
		public override string Name => "xr_devices";

		public override string Description
			=> "Every XR device reported by Unity (name, serial, characteristics, tracked state, pose) with the tracker-filter verdict for each.";

		public override ISchema Schema => new InputSchema()
			.Property<bool>("tracked_only", "Only devices whose isTracked flag is true.");

		protected override UniTask<IOutput> Run(IInput args) {
			var devices = XRDiagnostics.Devices(args.Get<bool>("tracked_only"));

			return UniTask.FromResult<IOutput>(OperatorOutput.Ok(new {
				count = devices.Count,
				devices,
				lines = devices.Select(d => d.Describe()).ToArray()
			}));
		}
	}

	/// <summary>Trackers as the game uses them, or every tracked device when asked.</summary>
	public sealed class XRTrackersOperator : XROperatorBase {
		public override string Name => "xr_trackers";

		public override string Description
			=> "Full-body trackers currently usable by the game. Set include_all to also list the tracked devices the filter rejects (with the rejection reason).";

		public override ISchema Schema => new InputSchema()
			.Property<bool>("include_all", "Include rejected tracked devices (HMD/controllers/handed/untracked).");

		protected override UniTask<IOutput> Run(IInput args) {
			var devices = XRDiagnostics.Trackers(args.Get<bool>("include_all"));

			var used = devices.Where(d => d.TrackerCandidate).ToArray();

			return UniTask.FromResult<IOutput>(OperatorOutput.Ok(new {
				count          = used.Length,
				listed         = devices.Count,
				rejected       = devices.Count - used.Length,
				allow_controller_flagged = Nox.XR.Runtime.Settings.TrackersIncludeControllerSetting.Value,
				devices,
				lines = devices.Select(d => d.Describe()).ToArray()
			}));
		}
	}

	/// <summary>Calibration and rig state.</summary>
	public sealed class XRFullBodyOperator : XROperatorBase {
		public override string Name => "xr_full_body";

		public override string Description
			=> "Full-body calibration state: stored tracker/bone bindings, live matches, effective range and (optionally) the rig targets.";

		public override ISchema Schema => new InputSchema()
			.Property<bool>("with_rig", "Include the rig bones (path, position, active) for the calibration bones.");

		protected override UniTask<IOutput> Run(IInput args) {
			var snapshot = XRDiagnostics.FullBody(args.Get<bool>("with_rig"));

			return UniTask.FromResult<IOutput>(OperatorOutput.Ok(new {
				snapshot,
				summary = snapshot.Describe(string.Empty)
			}));
		}
	}

	/// <summary>The diagnosis: what is wrong with the trackers right now.</summary>
	public sealed class XRDiagnoseOperator : XROperatorBase {
		public override string Name => "xr_diagnose";

		public override string Description
			=> "Diagnosis of the tracker situation: which loader is active, which devices were seen and why each one is or is not used as a full-body tracker.";

		protected override UniTask<IOutput> Run(IInput _) {
			var hints = XRDiagnostics.Hints();

			return UniTask.FromResult<IOutput>(OperatorOutput.Ok(new {
				hints,
				text = string.Join("\n", hints)
			}));
		}
	}

	/// <summary>Remotely drives the full-body calibration.</summary>
	public sealed class XRCalibrateOperator : XROperatorBase {
		public override string Name => "xr_calibrate";

		public override string Description
			=> "Drives the full-body calibration: start (show the trackers and match them), confirm (save), cancel, clear (forget) or status.";

		public override string[] RequiredPermissions => new[] { XRControl.ControlPermission };

		public override ISchema Schema => new InputSchema()
			.Property<string>("action", "start | confirm | cancel | clear | toggle | status", true);

		protected override UniTask<IOutput> Run(IInput args) {
			var calibration = FullBodyCalibration.Instance;
			if (calibration == null)
				return UniTask.FromResult<IOutput>(OperatorOutput.Error(
					"Full-body calibration is unavailable: the XR proxy is not created (no XR session or not the active controller)."
				));

			var action = (args.Get<string>("action") ?? string.Empty).Trim().ToLowerInvariant();
			switch (action) {
				case "start":
					calibration.StartCalibration();
					break;
				case "confirm":
				case "save":
					calibration.ConfirmCalibration();
					break;
				case "cancel":
					calibration.CancelCalibration();
					break;
				case "clear":
				case "reset":
					calibration.ClearCalibration();
					break;
				case "toggle":
					if (calibration.IsCalibrating) calibration.ConfirmCalibration();
					else calibration.StartCalibration();
					break;
				case "status":
				case "":
					break;
				default:
					return UniTask.FromResult<IOutput>(OperatorOutput.Error(
						$"Unknown action '{action}' (expected start, confirm, cancel, clear, toggle or status)."
					));
			}

			var snapshot = XRDiagnostics.FullBody();
			return UniTask.FromResult<IOutput>(OperatorOutput.Ok(new {
				action,
				calibrating = snapshot.Calibrating,
				has_calibration = snapshot.HasCalibration,
				bindings = snapshot.Bindings?.Length ?? 0,
				summary = snapshot.Describe(string.Empty)
			}));
		}
	}
}
