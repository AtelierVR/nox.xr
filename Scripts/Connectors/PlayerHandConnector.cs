using System.Collections.Generic;
using Autohand;
using Nox.CCK.Utils;
using UnityEngine;

namespace Nox.XR.Connectors
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

			Fallbacks[0].gameObject.SetActive(player.handLeft  == Fallbacks[0]);
			Fallbacks[1].gameObject.SetActive(player.handRight == Fallbacks[1]);
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
