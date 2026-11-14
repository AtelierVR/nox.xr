using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Nox.XR.Runtime.Settings;
using XRNearFarInteractor = UnityEngine.XR.Interaction.Toolkit.Interactors.NearFarInteractor;

namespace Nox.XR.Runtime.Connectors {
	public class NearFarInteractor : XRNearFarInteractor, IUIInteractor {
		/// <summary>
		/// Uses the toolkit's serialized Handedness field (also visible in its inspector).
		/// None disables input for this interactor.
		/// </summary>
		public InteractorHandedness Hand {
			get => handedness;
			set => handedness = value;
		}

		private bool _interactive = true;

		[SerializeField, Min(0f), Tooltip("Part of the joystick scroll given to the UI: below one, the lists scroll slower.")]
		private float uiScrollSpeed = 0.4f;

		private readonly List<Vector3> _lockedPoints = new();

		/// <summary>
		/// Freezes the UI raycast on <paramref name="worldPoints"/>: the ray may keep moving, the cursor stays where
		/// the caller put it. The caller provides the points again every frame, so the cursor can follow a moving
		/// element instead of staying at a fixed place in the world.
		/// </summary>
		public void HoldUiPointer(IReadOnlyList<Vector3> worldPoints) {
			_lockedPoints.Clear();

			if (worldPoints == null)
				return;

			for (var i = 0; i < worldPoints.Count; i++)
				_lockedPoints.Add(worldPoints[i]);
		}

		public void ReleaseUiPointer()
			=> _lockedPoints.Clear();

		/// <summary>
		/// Whether the UI pointer is currently held on an element instead of following the ray. A held point sits off the
		/// ray, so the ray has to be turned towards it for the line to stay straight.
		/// </summary>
		public bool HasHeldPointer
			=> _lockedPoints.Count > 1;

		void IUIInteractor.UpdateUIModel(ref TrackedDeviceModel model) {
			base.UpdateUIModel(ref model);

			var points = model.raycastPoints;

			if (_lockedPoints.Count < 2 || points == null)
				return;

			var first = _lockedPoints[0];
			var last  = _lockedPoints[_lockedPoints.Count - 1];
			var direction = last - first;

			model.position = first;

			if (direction.sqrMagnitude > 1e-8f)
				model.orientation = Quaternion.LookRotation(direction);

			// The UI raycast follows the points of the model, not its pose: they are what keeps the cursor in place.
			if (points.Count == _lockedPoints.Count) {
				for (var i = 0; i < points.Count; i++)
					points[i] = _lockedPoints[i];

				return;
			}

			points.Clear();
			points.AddRange(_lockedPoints);
		}

		/// <summary>
		/// Whether the ray may interact with anything.
		/// <para>
		/// Off stops both casters and cuts the UI interaction, which is what takes the ray out of every
		/// interaction, but the component stays <b>enabled</b>: the toolkit derives the visibility of the line
		/// from <c>isActiveAndEnabled</c> and <c>enableFarCasting</c>, so a disabled component hides the line in
		/// one frame while a stopped caster lets it play its retraction.
		/// </para>
		/// </summary>
		public bool Enable {
			get => _interactive;
			set {
				if (_interactive == value)
					return;

				_interactive = value;
				enabled = true;

				enableNearCasting = value;
				enableFarCasting = value;
				enableUIInteraction = value;
				allowHover = value;
				allowSelect = value;
			}
		}

		protected override void Awake() {
			// Nox owns the shared actions. Manual readers keep XRI from disabling them
			// when this interactor is disabled or the avatar is replaced.
			selectInput = CreateButtonReader();
			activateInput = CreateButtonReader();
			uiPressInput = CreateButtonReader();
			uiScrollInput = new XRInputValueReader<Vector2> {
				inputSourceMode = XRInputValueReader.InputSourceMode.ManualValue
			};
			base.Awake();
		}

		protected override void OnEnable() {
			ResetInputs();
			base.OnEnable();
		}

		protected override void OnDisable() {
			base.OnDisable();
			ResetInputs();
		}

		public override void PreprocessInteractor(XRInteractionUpdateOrder.UpdatePhase updatePhase) {
			if (updatePhase == XRInteractionUpdateOrder.UpdatePhase.Dynamic) {
				var side = handedness switch {
					InteractorHandedness.Left => "left",
					InteractorHandedness.Right => "right",
					_ => null
				};
				// A ray that must not interact eats no input either: its buttons stay released, so neither the
				// toolkit nor the UI module can act through it while a poke owns the gesture.
				UpdateButton(selectInput, _interactive && side != null ? Keybindings.GetFloatValue($"select.{side}") : 0f);
				UpdateButton(activateInput, _interactive && side != null ? Keybindings.GetFloatValue($"activate.{side}") : 0f);
				UpdateButton(uiPressInput, _interactive && side != null ? Keybindings.GetFloatValue($"press.{side}") : 0f);
				uiScrollInput.manualValue = _interactive && side != null && ScrollInputSettings.Mode != XRScrollInput.MovementOnly
					? Keybindings.GetVector2Value($"scroll.{side}") * uiScrollSpeed
					: Vector2.zero;
			}

			// The base class consumes button states during Dynamic preprocessing.
			base.PreprocessInteractor(updatePhase);
		}

		private static XRInputButtonReader CreateButtonReader()
			=> new() {
				inputSourceMode = XRInputButtonReader.InputSourceMode.ManualValue,
				manualFramePerformed = -1,
				manualFrameCompleted = -1
			};

		private static void UpdateButton(XRInputButtonReader reader, float value) {
			var pressed = value > 0.1f;
			if (pressed != reader.manualPerformed) {
				if (pressed)
					reader.manualFramePerformed = Time.frameCount;
				else
					reader.manualFrameCompleted = Time.frameCount;
			}
			reader.manualPerformed = pressed;
			reader.manualValue = value;
		}

		private void ResetInputs() {
			ResetButton(selectInput);
			ResetButton(activateInput);
			ResetButton(uiPressInput);
			uiScrollInput.manualValue = Vector2.zero;
		}

		private static void ResetButton(XRInputButtonReader reader) {
			reader.manualPerformed = false;
			reader.manualValue = 0f;
			reader.manualFramePerformed = -1;
			reader.manualFrameCompleted = -1;
		}
	}
}
