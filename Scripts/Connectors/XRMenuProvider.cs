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

			// Teleport the grabbable onto the palm so ForceGrab's raycast hits immediately
			// and GrabType.InstantGrab snaps without any travel animation.
			Grabbable.transform.position = hand.palmTransform.position;
			Grabbable.transform.rotation = hand.palmTransform.rotation;
			if (Grabbable.body != null) {
				Grabbable.body.position = hand.palmTransform.position;
				Grabbable.body.rotation = hand.palmTransform.rotation;
			}

			hand.TryGrab(Grabbable);
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