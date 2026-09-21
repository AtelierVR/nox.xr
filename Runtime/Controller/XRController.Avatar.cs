using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autohand;
using Nox.CCK.XR;
using Cysharp.Threading.Tasks;
using Nox.Avatars;
using Nox.Avatars.Camera;
using Nox.Avatars.Controllers;
using Nox.CCK.Players;
using Nox.CCK.Utils;
using Nox.Audio.Players;
using Nox.Sessions;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;
using Transform = UnityEngine.Transform;
using Nox.Controllers;
using Nox.Players;
using Nox.XR.Runtime.Connectors;
using Nox.XR.Runtime.Providers;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR;
using Unity.XR.CoreUtils;

namespace Nox.XR.Runtime {
	public partial class XRController {

	/// <summary>
	/// Avatar side of the XR controller: loading and attaching the avatar on the AutoHand player,
	/// the hands it needs, and the view height correction that follows from it.
	/// </summary>

		public IRuntimeAvatar GetAvatar()
			=> avatarLoader?.GetAvatar();

		public async UniTask<bool> SetAvatar(IRuntimeAvatar runtimeAvatar)
			=> avatarLoader != null && await avatarLoader.SetAvatar(runtimeAvatar);

		private async UniTask AutoFixViewHeight() {
			if (!autoFixViewHeight)
				return;

			// La hauteur recommandée vient de la taille de l'avatar : on attend qu'il soit
			// chargé, sans bloquer indéfiniment s'il n'en arrive aucun.
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(AutoFixWaitSeconds + 1));
			var token = timeout.Token;

			// Laisser le tracking et l'initialisation XR se stabiliser avant de juger la hauteur.
			await UniTask.Delay(TimeSpan.FromSeconds(1), cancellationToken: token)
				.SuppressCancellationThrow();

			await UniTask.WaitUntil(
					() => !this || !player || (avatarLoader && avatarLoader.GetAvatar() != null),
					cancellationToken: token
				)
				.SuppressCancellationThrow();

			if (!this || !player)
				return;

			var height = GetViewHeight();
			if (height >= MinPlausibleViewHeight)
				return;

			Logger.LogWarning(
				$"XR view started at {height:F2} m (below {MinPlausibleViewHeight:F2} m), "
				+ $"recentering to the recommended height ({GetRecommendedHeight():F2} m).",
				this
			);
			ReCenterAndReHeight();
		}

		private async UniTask StartupAutoHand() {
			// Vérification des références nulles
			if (!player) {
				// Un proxy détruit avant son premier Start() (bascule de contrôleur pendant
				// l'init XR) est un cas normal : on ne logue que si le composant est vivant.
				if (!this)
					return;

				// Sans aucune référence configurée, ce n'est pas le proxy : c'est le
				// XRController vide ajouté automatiquement par un [RequireComponent] sur un
				// GameObject enfant du proxy. On le neutralise sans bruit.
				if (avatarLoader == null && Menu == null && microphone == null) {
					Logger.LogWarning(
						$"{nameof(XRController)} on '{name}' is not configured (no player, avatarLoader, Menu "
						+ "or microphone): redundant component, likely auto-added by [RequireComponent] on a "
						+ "child of the proxy. It is disabled — remove it from the prefab.",
						this
					);
					enabled = false;
					return;
				}

				Logger.LogError($"XRController.player is null in StartupAutoHand on '{name}'.", this);
				return;
			}

			if (!player.bodyCollider) {
				Logger.LogError("XRController.player.bodyCollider is null in StartupAutoHand");
				return;
			}

			player.bodyCollider.material = new PhysicsMaterial {
			    dynamicFriction = 0f,
			    staticFriction  = 0f,
			    bounciness      = 0f,
			    frictionCombine = PhysicsMaterialCombine.Maximum,
			    bounceCombine   = PhysicsMaterialCombine.Average
			};

			if (interactions == null || interactions.Length == 0) {
				Logger.LogWarning("XRController.interactions is null or empty in StartupAutoHand");
				return;
			}

			foreach (var interaction in interactions) {
				if (!interaction)
					continue;
				interaction.gameObject.SetActive(false);
				foreach (var member in interaction.startingGroupMembers)
					if (member is MonoBehaviour mb)
						mb.gameObject.SetActive(false);
			}

			await UniTask.NextFrame();

			foreach (var interaction in interactions) {
				if (!interaction)
					continue;
				interaction.gameObject.SetActive(true);
				foreach (var member in interaction.startingGroupMembers)
					if (member is MonoBehaviour mb)
						mb.gameObject.SetActive(true);
			}

			XRInputs.Provider = new AutoHandProvider();
		}



		public async UniTask<IRuntimeAvatar> SetAvatar(Identifier identifier, Action<string, float> progress = null)
			=> avatarLoader != null ? await avatarLoader.SetAvatar(identifier, progress) : null;

		public async UniTask<IRuntimeAvatar> ReloadAvatar(Action<string, float> progress = null)
			=> avatarLoader != null ? await avatarLoader.ReloadAvatar(progress) : null;


		// ReSharper disable Unity.PerformanceAnalysis
	}
}
