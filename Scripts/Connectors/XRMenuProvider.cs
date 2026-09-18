using System;
using Autohand;
using Cysharp.Threading.Tasks;
using Nox.UI;
using UnityEngine;
using UnityEngine.XR;
using Hand = Autohand.Hand;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Connectors {
	public class XRMenuProvider : MonoBehaviour, IMenuProvider, IDisposable {
		public RectTransform Container;
		public Grabbable Grabbable;
		public AutoHandPlayer AutoHandPlayer;

		public XRNode LastUsedHand = XRNode.LeftHand;

		public IMenu Menu;

		RectTransform IMenuProvider.Container
			=> Container;

		public bool Active {
			get => gameObject.activeSelf;
			set => gameObject.SetActive(value);
		}

		public async UniTask<bool> Generate() {
			Menu = await Client.UiAPI.Make(this);

			if (Menu == null) {
				Logger.LogError("Failed to create XR proxy menu");
				return false;
			}

			Menu.Active = false;

			Keybindings.KeyFloatEvent.AddListener(OnKey);

			return true;
		}

		private void OnKey(string key, float @new, float old) {
			switch (key) {
				case "menu" when @new > 0 && old == 0:
					ToggleMenu(LastUsedHand);
					break;
				case "menu.left" when @new > 0 && old == 0:
					ToggleMenu(XRNode.LeftHand);
					break;
				case "menu.right" when @new > 0 && old == 0:
					ToggleMenu(XRNode.RightHand);
					break;
			}
		}

		private void ToggleMenu(XRNode node) {
			LastUsedHand = node;

			var hand = XRNode.LeftHand == node
				? AutoHandPlayer.handLeft
				: AutoHandPlayer.handRight;

			if (Menu.Active)
				Close();
			else
				Open(hand);
		}

		public void Open(Hand hand) {
			if (Menu == null) {
				Logger.LogError("Menu is not generated");
				return;
			}

			Menu.Active = true;

			// Position the menu in front of the main camera (head level),
			// NOT attached to the hand. This avoids the menu inheriting
			// the hand's twisted rotation when the user doesn't hold their hand straight.
			// d = distance forward from camera in meters
			const float menuDistance = 0.5f;
			var camera = Camera.main;
			if (camera != null) {
				var forward = camera.transform.forward;
				forward.y = 0f; // keep it horizontal (no pitch tilt)
				forward.Normalize();

				var position = camera.transform.position + forward * menuDistance;
				// Always face the camera so the menu is readable regardless of head orientation
				const float rollMargin = 20f; // degrees
				var rotation = Quaternion.LookRotation(
					position - camera.transform.position,
					Vector3.up
				);
				// If player is mostly upright, force menu to be perfectly level (no roll)
				float roll = camera.transform.rotation.eulerAngles.z;
				if (roll > 180f) roll -= 360f;
				if (Mathf.Abs(roll) < rollMargin) {
					// Keep pitch and yaw, set roll to 0
					Vector3 euler = rotation.eulerAngles;
					rotation = Quaternion.Euler(euler.x, euler.y, 0);
				}

				Grabbable.transform.SetPositionAndRotation(position, rotation);
			} else {
				// Fallback: position in front of the hand if no main camera found
				Grabbable.transform.SetPositionAndRotation(
					hand.palmTransform.position + Vector3.forward * menuDistance, 
					Quaternion.identity
				);
			}

			if (Grabbable.body != null) {
				Grabbable.body.position = Grabbable.transform.position;
				Grabbable.body.rotation = Grabbable.transform.rotation;
			}

			// No need to try-grab the grabbable when opening the menu;
			// the menu is simply placed in world space in front of the player.
		}

		public void Close() {
			if (Menu == null) {
				Logger.LogError("Menu is not generated");
				return;
			}

			Menu.Active = false;
			Grabbable.HandsRelease();
		}

		public void Dispose() {
			Keybindings.KeyFloatEvent.RemoveListener(OnKey);
			Menu?.Dispose();
			Menu = null;
		}
	}
}