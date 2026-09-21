using System.Collections.Generic;
using Autohand;
using Nox.CCK.Utils;
using UnityEngine;

namespace Nox.XR.Runtime.Connectors
{
	public class PlayerHandConnector : MonoBehaviour
	{
		public AutoHandPlayer player;
		public Hand[] Fallbacks;

		private void OnValidate()
		{
			if (Fallbacks == null || Fallbacks.Length != 2)
				Fallbacks = new Hand[2];
			else if (Fallbacks[0] != null && !Fallbacks[0].left)
				(Fallbacks[1], Fallbacks[0]) = (Fallbacks[0], Fallbacks[1]);
			else if (Fallbacks[1] != null && Fallbacks[1].left)
				(Fallbacks[0], Fallbacks[1]) = (Fallbacks[1], Fallbacks[0]);
		}

		/// <summary>
		/// Pose brute du contrôleur suivi pour un côté. Le <c>follow</c> d'une main de remplacement
		/// AutoHand n'est pas la pose du contrôleur : il porte un offset de rotation propre au
		/// prefab (85° en X pour les RobotHands). C'est donc au parent qu'il faut s'adresser pour
		/// récupérer la pose réelle, sur laquelle on applique ensuite le pivot de l'avatar.
		/// </summary>
		public Transform GetTrackedController(bool left)
		{
			var fallback = left ? Fallbacks[0] : Fallbacks[1];
			if (fallback == null) return null;

			var follow = fallback.follow;
			if (follow == null) return null;

			return follow.parent != null ? follow.parent : follow;
		}

		public void Set(Hand h1, Hand h2)
		{
			var l = (h1?.left ?? false) ? h1 : h2;
			var r = (h1?.left ?? false) ? h2 : h1;

			if (l != null) {
				Merge(l, Fallbacks[0]);
				SetupFingers(l);
			}

			if (r != null) {
				Merge(r, Fallbacks[1]);
				SetupFingers(r);
			}

			player.handLeft  = l ?? Fallbacks[0];
			player.handRight = r ?? Fallbacks[1];

			// Les mains de fallback ne doivent rester visibles QUE si l'avatar n'a pas
			// fourni de main. Sinon on les désactive complètement : leur rôle de corps
			// physique est repris par les duplicatas des mains de l'avatar.
			ApplyFallbackVisibility(l, r);
		}

		/// <summary>
		/// Active la main de fallback uniquement quand l'avatar ne fournit pas la main
		/// correspondante, et coupe son rendu sinon.
		/// </summary>
		private void ApplyFallbackVisibility(Hand leftAvatar, Hand rightAvatar)
		{
			ApplyFallback(Fallbacks[0], leftAvatar != null);
			ApplyFallback(Fallbacks[1], rightAvatar != null);
		}

		/// <summary>
		/// La main de remplacement AutoHand ne sert QUE si l'avatar ne fournit pas la main
		/// correspondante : dans ce cas on la désactive complètement. Le corps physique de la main
		/// est alors un duplicata de la main de l'avatar, créé par <c>NoxAutoHandVRIK</c> dans le
		/// dossier « Hands » (mêmes colliders, mêmes pokes, même échelle).
		/// </summary>
		private static void ApplyFallback(Hand fallback, bool hidden)
		{
			if (fallback == null) return;

			if (fallback.gameObject.activeSelf == hidden)
				fallback.gameObject.SetActive(!hidden);

			foreach (var renderer in fallback.GetComponentsInChildren<Renderer>(true))
				renderer.enabled = !hidden;
		}

		private void SetupFingers(Hand hand)
		{
			if (hand == null) return;
			// Retrieve all AutoHand.Finger components in the hand's hierarchy
			var fingers = hand.GetComponentsInChildren<Finger>(true);
			var pokes = new List<(string key, PokeInteractor poke)>(fingers.Length);
			foreach (var finger in fingers) {
				var connector = finger.gameObject.GetOrAddComponent<FingerKeybindConnector>();

				string handSide = hand.left ? "left" : "right";
				string typeName = finger.fingerType.ToString().ToLower();
				var bindKey = $"finger.{handSide}.{typeName}";
				connector.BindKey = bindKey;

				if (finger.tip != null) {
					var poke = finger.tip.gameObject.GetOrAddComponent<PokeInteractor>();
					poke.Radius = finger.tipRadius;
					pokes.Add((bindKey, poke));
				}
			}

			var handPoke = hand.gameObject.GetOrAddComponent<HandPokeConnector>();
			handPoke.Setup(hand, pokes.ToArray());
		}

		public static void Merge(Hand hand, Hand original)
		{
			if (hand == null || hand == original) return;

			hand.follow = original.follow;

			hand.reachDistance                  = original.reachDistance;
			hand.enableMovement                 = original.enableMovement;
			hand.throwPower                     = original.throwPower;
			hand.gentleGrabSpeed                = original.gentleGrabSpeed;
			hand.advancedFollowSettings         = original.advancedFollowSettings;
			hand.enableIK                       = original.enableIK;
			hand.swayStrength                   = original.swayStrength;
			hand.gripOffset                     = original.gripOffset;
			hand.throwVelocityExpireTime        = original.throwVelocityExpireTime;
			hand.throwAngularVelocityExpireTime = original.throwAngularVelocityExpireTime;
			hand.fingerBendSteps                = original.fingerBendSteps;
			hand.usingPoseAreas                 = original.usingPoseAreas;

			hand.usingHighlight               = original.usingHighlight;
			hand.highlightLayers              = original.highlightLayers;
			hand.defaultHighlight             = original.defaultHighlight;
			hand.noHandFriction               = original.noHandFriction;
			hand.ignoreGrabCheckLayers        = original.ignoreGrabCheckLayers;
			hand.grabType                     = original.grabType;
			hand.grabCurve                    = original.grabCurve;
			hand.minGrabTime                  = original.minGrabTime;
			hand.maxGrabTime                  = original.maxGrabTime;
			hand.velocityGrabHandAmplifier    = original.velocityGrabHandAmplifier;
			hand.velocityGrabObjectAmplifier  = original.velocityGrabObjectAmplifier;
			hand.grabOpenHandPoint            = original.grabOpenHandPoint;
			hand.poseIndex                    = original.poseIndex;
		}

		public void Clear()
			=> Set(null, null);
	}
}
