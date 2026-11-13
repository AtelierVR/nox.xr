using UnityEngine;

namespace Nox.XR.Player {
	public partial class Player {
		/* ------------------------------------------------------------------ */
		/* Grounding                                                           */
		/* ------------------------------------------------------------------ */

		protected virtual void Ground() {
			SetGrounded(false);
			lastGroundHit = new RaycastHit();

			if (_tempDisableGrounding || !useGrounding)
				return;

			var scale = transform.lossyScale.x > transform.lossyScale.z ? transform.lossyScale.x : transform.lossyScale.z;
			var maxStep = maxStepHeight;
			maxStep *= scale;

			_highestPoint = -1f;

			if (useFootGrounding && TryFootGrounding(maxStep))
				SetGrounded(true);
			else
				CapsuleGround(maxStep);

			if (_isGrounded) {
				body.linearVelocity = new Vector3(body.linearVelocity.x, 0f, body.linearVelocity.z);

				if (useSmoothStep) {
					var currentY = body.position.y;
					var targetY = lastGroundHit.point.y;
					var newY = Mathf.Lerp(currentY, targetY, Time.fixedDeltaTime * stepSmoothSpeed);
					body.position = new Vector3(body.position.x, newY, body.position.z);
				} else {
					body.position = new Vector3(body.position.x, lastGroundHit.point.y, body.position.z);
				}

				transform.position = body.position;
			}

			body.useGravity = !_isGrounded;
		}

		private bool TryFootGrounding(float maxStep) {
			var found = false;

			foreach (var part in _parts.Values) {
				if (part is not Foot foot || !foot.PartEnabled)
					continue;

				if (!foot.TryGetGround(groundLayerMask, maxStep + groundingPenetrationOffset, maxStepAngle, out var hit))
					continue;

				var distance = hit.point.y - transform.position.y;
				if (distance < -groundingPenetrationOffset || distance > maxStep + groundingPenetrationOffset)
					continue;

				if (distance <= _highestPoint)
					continue;

				_highestPoint = distance;
				lastGroundHit = hit;
				found = true;
			}

			return found;
		}

		private void CapsuleGround(float maxStep) {
			// A single sphere at the bottom of the body is enough to know whether we touch the ground:
			// sweeping the whole capsule shape only adds cost and false positives.
			var colliderScale = floorCollider.transform.lossyScale;
			var radius = floorCollider.radius * Mathf.Max(colliderScale.x, colliderScale.y, colliderScale.z);
			var bottom = floorCollider.transform.TransformPoint(floorCollider.center) + Vector3.down * radius;
			var origin = bottom + Vector3.up * (maxStep + radius);
			var distance = maxStep + radius * 2f + groundingPenetrationOffset;

			var hitCount = Physics.SphereCastNonAlloc(
				origin, radius, Vector3.down,
				_hitsNonAlloc, distance,
				groundLayerMask,
				QueryTriggerInteraction.Ignore
			);

			CheckGroundHits(hitCount, bottom, maxStep);

			if (!_isGrounded && hitCount > 0) {
				// Mesh colliders only report the highest hit: retry with a smaller sphere just below the feet.
				radius *= 0.5f;
				origin = bottom + Vector3.up * (maxStep + radius);
				distance = maxStep + radius * 2f + groundingPenetrationOffset;
				hitCount = Physics.SphereCastNonAlloc(
					origin, radius, Vector3.down,
					_hitsNonAlloc, distance,
					groundLayerMask,
					QueryTriggerInteraction.Ignore
				);

				CheckGroundHits(hitCount, bottom, maxStep);
			}
		}

		private void CheckGroundHits(int hitCount, Vector3 point2, float maxStep) {
			for (var i = 0; i < hitCount; i++) {
				var hit = _hitsNonAlloc[i];
				if (hit.collider == floorCollider)
					continue;
				if (hit.point.y < point2.y || hit.point.y > point2.y + maxStep + groundingPenetrationOffset)
					continue;

				var stepAngle = Vector3.Angle(hit.normal, Vector3.up);
				var distance = hit.point.y - transform.position.y;
				if (stepAngle >= maxStepAngle || distance <= _highestPoint)
					continue;

				SetGrounded(true);
				_highestPoint = distance;
				lastGroundHit = hit;
			}
		}

		public bool IsGrounded()
			=> _isGrounded;

		/// <summary>
		/// Sets the grounded state and announces it when it changes: the consumers are pushed the new value
		/// instead of polling it every frame.
		/// </summary>
		private void SetGrounded(bool grounded) {
			if (_isGrounded == grounded)
				return;

			_isGrounded = grounded;
			OnGrounded.Invoke(grounded);
		}

		public void ToggleFlying() {
			useGrounding = !useGrounding;
			body.useGravity = useGrounding;
		}

		protected virtual void UpdatePlayerHeight() {
			if (!Head || !Head.PartEnabled)
				return;

			var crouch = IsCrouching;
			if (crouch != _lastCrouching) {
				if (_lastCrouching)
					heightOffset += _lastCrouchingHeight;
				if (!_lastCrouching)
					heightOffset -= crouchHeight;

				_lastCrouching = crouch;
				_lastCrouchingHeight = crouchHeight;
			}
		}

		protected void UpdatePlatform() {
			if (!allowPlatforms || !_isGrounded || !lastGroundHit.transform)
				return;

			if ((platformingLayerMask & (1 << lastGroundHit.collider.gameObject.layer)) == 0)
				return;

			if (!lastGroundHit.transform.Equals(lastPlatformingHit.transform)) {
				lastPlatformingHit = lastGroundHit;
				_lastPlatformPosition = lastPlatformingHit.transform.position;
				_lastPlatformRotation = lastPlatformingHit.transform.rotation;
				return;
			}

			if (lastPlatformingHit.transform.position == _lastPlatformPosition
				&& lastPlatformingHit.transform.rotation.eulerAngles == _lastPlatformRotation.eulerAngles)
				return;

			var deltaPos = lastPlatformingHit.transform.position - _lastPlatformPosition;
			var deltaRot = lastPlatformingHit.transform.rotation * Quaternion.Inverse(_lastPlatformRotation);

			trackingContainer.RotateAround(Head.Position, Vector3.up, deltaRot.eulerAngles.y);

			transform.position += deltaPos;
			body.position = transform.position;

			trackingContainer.position += deltaPos;
			_lastUpdatePosition = transform.position;

			_targetTrackedPos = new Vector3(trackingContainer.position.x, _targetTrackedPos.y + deltaPos.y, trackingContainer.position.z);
			_lastPlatformPosition = lastPlatformingHit.transform.position;
			_lastPlatformRotation = lastPlatformingHit.transform.rotation;
		}

		public void Jump(float jumpPower = 1f) {
			if (!_isGrounded)
				return;

			DisableGrounding(0.1f);
			body.useGravity = true;
			body.AddForce(Vector3.up * jumpPower, ForceMode.VelocityChange);

			OnJump.Invoke();
		}

		public void DisableGrounding(float seconds) {
			if (_disableGroundingRoutine != null)
				StopCoroutine(_disableGroundingRoutine);
			_disableGroundingRoutine = StartCoroutine(DisableGroundingRoutine(seconds));
		}

		private System.Collections.IEnumerator DisableGroundingRoutine(float seconds) {
			_tempDisableGrounding = true;
			SetGrounded(false);
			yield return new WaitForSeconds(seconds);
			_tempDisableGrounding = false;
		}

		public void AddVelocity(Vector3 force, ForceMode mode = ForceMode.Acceleration)
			=> body.AddForce(force, mode);
	}
}
