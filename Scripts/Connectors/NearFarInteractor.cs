using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using XRNearFarInteractor = UnityEngine.XR.Interaction.Toolkit.Interactors.NearFarInteractor;

namespace Nox.XR.Connectors {
	public class NearFarInteractor : XRNearFarInteractor {
		/// <summary>
		/// Uses the toolkit's serialized Handedness field (also visible in its inspector).
		/// None disables input for this interactor.
		/// </summary>
		public InteractorHandedness Hand {
			get => handedness;
			set => handedness = value;
		}

		public bool Enable {
			get => enabled;
			set => enabled = value;
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
				UpdateButton(selectInput, side == null ? 0f : Keybindings.GetFloatValue($"select.{side}"));
				UpdateButton(activateInput, side == null ? 0f : Keybindings.GetFloatValue($"activate.{side}"));
				UpdateButton(uiPressInput, side == null ? 0f : Keybindings.GetFloatValue($"press.{side}"));
				uiScrollInput.manualValue = side == null ? Vector2.zero : Keybindings.GetVector2Value($"scroll.{side}");
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
