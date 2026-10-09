using System;
using Autohand;
using Cysharp.Threading.Tasks;
using Nox.UI;
using UnityEngine;
using UnityEngine.XR;
using Hand = Autohand.Hand;
using Nox.CCK.Nameplate;
using Logger = Nox.CCK.Utils.Logger;
using Keys = Nox.CCK.Nameplate.Constants;

namespace Nox.XR.Runtime.Connectors {
	public class XRMenuProvider : MonoBehaviour, IMenuProvider, IDisposable {
		public RectTransform Container;
		public Grabbable Grabbable;
		public XRController Controller;

		public XRNode LastUsedHand = XRNode.LeftHand;

		public IMenu Menu;

		/// <summary>
		/// Use the menu's authored AutoHand grab pose (the <c>GrabbablePose</c> on the menu prefab).
		/// <para>
		/// Off by default: that pose was captured with the AutoHand <c>RobotHand</c> prefab, and applying
		/// it to an avatar hand (different finger local frames and hand pivot) twists the fingers and
		/// mis-orients the panel. With it off, AutoHand wraps the fingers around the panel procedurally
		/// (see <c>Hand.AutoPose</c>), which adapts to any hand.
		/// </para>
		/// </summary>
		[Tooltip("Use the menu's authored AutoHand grab pose. Off: the fingers wrap procedurally.")]
		public bool useGrabPose = false;

		/// <summary>
		/// The menu is grabbed with the player's hands, so a pose authored for the AutoHand RobotHand
		/// cannot be reused: clear it before any grab (the pose is only read on grab, so doing it once
		/// at spawn is enough).
		/// </summary>
		private void Awake() {
			if (!useGrabPose && Grabbable != null)
				foreach (var pose in Grabbable.GetComponentsInChildren<GrabbablePose>(true))
					pose.poseEnabled = false;
		}

		RectTransform IMenuProvider.Container
			=> Container;

		public bool Active {
			get => Grabbable.gameObject.activeSelf;
			set => Grabbable.gameObject.SetActive(value);
		}

		/// <summary>
		/// Dernières valeurs des touches de menu, pour ne basculer qu'au front montant : un binding
		/// XR est un axe, pas un bouton.
		/// </summary>
		private float _menuLeft;
		private float _menuRight;

		/// <summary>
		/// Dernière visibilité de nameplates poussée (réessayée tant que la plaque du contrôleur
		/// n'existe pas encore).
		/// </summary>
		private bool? _nameplateVisible;

		public async UniTask<bool> Generate() {
			Menu = await Client.UiAPI.Make(this);

			if (Menu == null) {
				Logger.LogError("Failed to create XR proxy menu");
				return false;
			}

			Menu.Active = false;

			return true;
		}

		/// <summary>
		/// Ouvre/ferme le menu sur le front montant des bindings de menu, relus auprès du runtime
		/// XR actif.
		/// </summary>
		private void Update() {
			// Les nameplates ne s'affichent que menu ouvert : on synchronise dès la première frame
			// (le menu démarre fermé) et tant que la plaque du contrôleur n'est pas créée.
			SyncNameplate(Menu != null && Menu.Active);

			if (Menu == null)
				return;

			var left = Keybindings.GetFloatValue("menu.left");
			if (left > 0.1f && _menuLeft <= 0.1f)
				ToggleMenu(XRNode.LeftHand);
			_menuLeft = left;

			var right = Keybindings.GetFloatValue("menu.right");
			if (right > 0.1f && _menuRight <= 0.1f)
				ToggleMenu(XRNode.RightHand);
			_menuRight = right;
		}

		private void ToggleMenu(XRNode node) {
			LastUsedHand = node;

			var hand = XRNode.LeftHand == node
				? Controller.player.handLeft
				: Controller.player.handRight;

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

			// Nameplates are only shown while a menu is open.
			SyncNameplate(true);

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

			// Nameplates are only shown while a menu is open.
			SyncNameplate(false);
		}

		/// <summary>
		/// Aligne la visibilité de la plaque du client local sur l'état du menu : elle ne s'affiche que
		/// lorsque le menu est ouvert. Les plaques des joueurs distants ne sont pas concernées (elles
		/// sont pilotées par les scripts).
		/// </summary>
		private void SyncNameplate(bool visible) {
			if (_nameplateVisible == visible)
				return;

			var plate = Controller != null ? Controller.Nameplate : null;
			if (plate == null || !plate.IsAlive())
				return;

			_nameplateVisible = visible;
			plate.Set(Keys.VISIBLE, visible);
		}

		public void Dispose() {
			Menu?.Dispose();
			Menu = null;
		}
	}
}