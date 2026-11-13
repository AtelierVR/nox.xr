using System;
using Nox.XR.Bindings;
using Nox.XR.Runtime;
using Nox.XR.Runtime.Settings;
using UnityEngine;

namespace Nox.XR.Player {
	/// <summary>What a <see cref="PlayerMovementDriver"/> drives on its player.</summary>
	[Flags]
	public enum PlayerMovementFlags {
		/// <summary>Nothing: the driver only carries the movement options onto the player.</summary>
		None = 0,

		/// <summary>The move axis (left stick) walks the body, relative to the head.</summary>
		Move = 1 << 0,

		/// <summary>The turn axis (right stick) rotates the body, continuously or by steps.</summary>
		Turn = 1 << 1,

		/// <summary>The rising edge of the jump binding makes the player jump.</summary>
		Jump = 1 << 2,

		/// <summary>
		/// The player flies instead of walking: grounding and gravity are off, and the move axis follows the
		/// whole view rather than its yaw, so looking up and pushing the stick climbs.
		/// </summary>
		Fly = 1 << 3,

		/// <summary>What a walking player needs: move, turn and jump.</summary>
		Walk = Move | Turn | Jump,

		/// <summary>Everything the driver can do.</summary>
		All = Move | Turn | Jump | Fly,
	}

	/// <summary>
	/// Drives the movement / turn / jump bindings of a <see cref="Player"/>, and carries the movement
	/// options of the settings onto it.
	/// <para>
	/// Pose tracking is handled per part by <see cref="XRFollow"/> components placed next to the parts;
	/// this component only owns the input bindings, so it stays independent from the tracking stack.
	/// </para>
	/// <para>
	/// The <see cref="Player"/> itself never reads input: locomotion is the left stick
	/// (<see cref="XRBinding.Move"/>, relative to the head) and the right stick
	/// (<see cref="XRBinding.Turn"/>). How that turn axis is applied belongs to the player, and comes
	/// from <see cref="MovementSettings"/>: continuous turn while the stick is tilted, or one fixed step
	/// per flick (<see cref="MovementSettings.TurnMode"/>), with
	/// <see cref="MovementSettings.SnapTurnAngle"/> and <see cref="MovementSettings.SmoothTurnSpeed"/>.
	/// Those options are always applied, so the menu is felt immediately.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Player/Player XR Input")]
	[DefaultExecutionOrder(20)]
	public class PlayerMovementDriver : MonoBehaviour {
		[SerializeField]
		private Player player;

		[SerializeField, Tooltip("Which movements this driver feeds from the XR bindings: move, turn, jump, fly.")]
		private PlayerMovementFlags movements = PlayerMovementFlags.Walk;

		private float _jump;

		public Player Player {
			get => player;
			set {
				player = value;
				if (isActiveAndEnabled)
					ApplyFlight();
			}
		}

		/// <summary>The movements the driver feeds. Written straight away while the component is active.</summary>
		public PlayerMovementFlags Movements {
			get => movements;
			set {
				if (movements == value)
					return;

				movements = value;
				if (isActiveAndEnabled)
					ApplyFlight();
			}
		}

		/// <summary>Whether the move axis walks the body.</summary>
		public bool CanMove {
			get => Has(PlayerMovementFlags.Move);
			set => Set(PlayerMovementFlags.Move, value);
		}

		/// <summary>Whether the turn axis rotates the body.</summary>
		public bool CanTurn {
			get => Has(PlayerMovementFlags.Turn);
			set => Set(PlayerMovementFlags.Turn, value);
		}

		/// <summary>Whether the jump binding makes the player jump.</summary>
		public bool CanJump {
			get => Has(PlayerMovementFlags.Jump);
			set => Set(PlayerMovementFlags.Jump, value);
		}

		/// <summary>Whether the player keeps flying (no grounding, no gravity) instead of walking.</summary>
		public bool CanFly {
			get => Has(PlayerMovementFlags.Fly);
			set => Set(PlayerMovementFlags.Fly, value);
		}

		private bool Has(PlayerMovementFlags flag)
			=> (movements & flag) != 0;

		private void Set(PlayerMovementFlags flag, bool on) {
			var value = on ? movements | flag : movements & ~flag;
			if (value == movements)
				return;

			movements = value;
			if (isActiveAndEnabled)
				ApplyFlight();
		}

		private void OnEnable() {
			Listen();
			ApplyMovementSettings();
			ApplyFlight();
		}

		private void OnDisable()
			=> StopListening();

		private void Update() {
			if (!player)
				return;

			player.Move(CanMove ? Keybindings.GetVector2Value(XRBinding.Move) : Vector2.zero);
			player.Turn(CanTurn ? Keybindings.GetVector2Value(XRBinding.Turn).x : 0f);

			if (!CanJump) {
				_jump = 0f;
				return;
			}

			var jump = Keybindings.GetFloatValue(XRBinding.Jump);
			if (jump > 0.1f && _jump <= 0.1f)
				player.Jump();
			_jump = jump;
		}

		/// <summary>
		/// Applies the <see cref="PlayerMovementFlags.Fly"/> flag. Flying is grounding turned off on the player: it
		/// also drops gravity, and makes the move axis follow the whole view instead of its yaw, so pushing the
		/// stick while looking up climbs. Only written when the mode has to change, so it never fights a script
		/// that owns the grounding.
		/// </summary>
		private void ApplyFlight() {
			if (!player || player.useGrounding == !CanFly)
				return;

			player.useGrounding = !CanFly;

			// While flying nothing else touches gravity (grounding returns early), so the flag holds.
			if (player.body)
				player.body.useGravity = !CanFly;
		}

		private void Listen() {
			MovementSettings.TurnModeChanged.RemoveListener(OnTurnModeChanged);
			MovementSettings.SnapTurnAngleChanged.RemoveListener(OnTurnOptionChanged);
			MovementSettings.SmoothTurnSpeedChanged.RemoveListener(OnTurnOptionChanged);

			MovementSettings.TurnModeChanged.AddListener(OnTurnModeChanged);
			MovementSettings.SnapTurnAngleChanged.AddListener(OnTurnOptionChanged);
			MovementSettings.SmoothTurnSpeedChanged.AddListener(OnTurnOptionChanged);
		}

		private void StopListening() {
			MovementSettings.TurnModeChanged.RemoveListener(OnTurnModeChanged);
			MovementSettings.SnapTurnAngleChanged.RemoveListener(OnTurnOptionChanged);
			MovementSettings.SmoothTurnSpeedChanged.RemoveListener(OnTurnOptionChanged);
		}

		private void OnTurnModeChanged(XRTurnMode _)
			=> ApplyMovementSettings();

		private void OnTurnOptionChanged(float _)
			=> ApplyMovementSettings();

		/// <summary>
		/// Copies the persisted options onto the player: the prefab keeps its own values, the settings win
		/// as soon as this component runs, so changing the rotation mode in the menu is felt immediately.
		/// </summary>
		private void ApplyMovementSettings() {
			if (!player)
				return;

			player.rotationType = MovementSettings.TurnMode == XRTurnMode.Smooth
				? PlayerRotationType.Smooth
				: PlayerRotationType.Snap;
			player.snapTurnAngle = MovementSettings.SnapTurnAngle;
			player.smoothTurnSpeed = MovementSettings.SmoothTurnSpeed;
		}
	}
}
