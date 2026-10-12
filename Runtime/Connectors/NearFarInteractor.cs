using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using XRNearFarInteractor = UnityEngine.XR.Interaction.Toolkit.Interactors.NearFarInteractor;

namespace Nox.XR.Runtime.Connectors {
	public class NearFarInteractor : XRNearFarInteractor {
		/// <summary>
		/// Uses the toolkit's serialized Handedness field (also visible in its inspector).
		/// None disables input for this interactor.
		/// </summary>
		public InteractorHandedness Hand {
			get => handedness;
			set => handedness = value;
		}

		private bool _interactive = true;

		/// <summary>
		/// Whether the ray may interact with anything.
		/// <para>
		/// Off <b>disables the component</b>, which is the only thing that really takes the ray out. XRI's
		/// <c>isCurveActive</c> - the value the line visual reads to decide whether to draw - is derived from
		/// <c>isActiveAndEnabled</c> and <c>enableFarCasting</c>, never from the interaction layers. Cutting
		/// the layers, <c>allowHover</c> and <c>allowSelect</c> alone therefore left the ray drawn (and the
		/// casters casting) while a poke owned the gesture: measured live, the <c>LineRenderer</c> stayed
		/// enabled with the layers at 0, and went off on the next render only once the component was
		/// disabled. A real disable also unregisters the interactor from the interaction manager and from the
		/// UI module, so it can neither hover, select nor click at a distance until it is enabled again.
		/// </para>
		/// </summary>
		public bool Enable {
			get => _interactive;
			set {
				if (_interactive == value)
					return;

				_interactive = value;
				enabled = value;
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
				uiScrollInput.manualValue = _interactive && side != null ? Keybindings.GetVector2Value($"scroll.{side}") : Vector2.zero;
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
