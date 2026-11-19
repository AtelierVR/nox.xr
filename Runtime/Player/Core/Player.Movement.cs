using UnityEngine;

namespace Nox.XR.Player {
	public partial class Player {
		/// <summary>Smallest horizontal speed change that makes the player announce a move, in m/s.</summary>
		private const float MIN_NOTIFY_VELOCITY = 0.01f;

		/* ------------------------------------------------------------------ */
		/* Input                                                               */
		/* ------------------------------------------------------------------ */

		/// <summary>Sets the move direction for this fixed update.</summary>
		public virtual void Move(Vector2 axis, bool useDeadzone = true, bool useRelativeDirection = false) {
			_moveDirection.x = !useDeadzone || Mathf.Abs(axis.x) > movementDeadzone ? axis.x : 0f;
			_moveDirection.z = !useDeadzone || Mathf.Abs(axis.y) > movementDeadzone ? axis.y : 0f;

			if (useRelativeDirection)
				_moveDirection = transform.rotation * _moveDirection;
		}

		public virtual void Turn(float turnAxis) {
			_turningAxis = Mathf.Abs(turnAxis) > turnDeadzone ? turnAxis : 0f;
		}

		/* ------------------------------------------------------------------ */
		/* Movement                                                            */
		/* ------------------------------------------------------------------ */

		protected virtual void UpdateRigidbody() {
			var move = AlterDirection(_moveDirection);
			var yVelocity = body.linearVelocity.y;

			if (move != Vector3.zero && CanInputMove()) {
				var newVelocity = Vector3.MoveTowards(body.linearVelocity, move * maxMoveSpeed, moveAcceleration * Time.fixedDeltaTime);
				if (newVelocity.magnitude > maxMoveSpeed)
					newVelocity = newVelocity.normalized * maxMoveSpeed;
				body.linearVelocity = newVelocity;
			}

			if (move.magnitude <= movementDeadzone && _isGrounded)
				body.linearVelocity *= Mathf.Clamp01(1f - groundedDrag * Time.fixedDeltaTime);
			else if (!useGrounding)
				body.linearVelocity *= Mathf.Clamp01(1f - flyingDrag * Time.fixedDeltaTime);

			if (body.useGravity)
				body.linearVelocity = new Vector3(body.linearVelocity.x, yVelocity, body.linearVelocity.z);

			NotifyMoveChanged();
		}

		/// <summary>
		/// Velocity of the body for the locomotion consumers: the rigidbody velocity plus the head-driven catch-up.
		/// <para>
		/// A room-scale walk moves the body by <see cref="SyncBodyHead"/>, which never appears in
		/// <c>body.linearVelocity</c>: without the second term the avatar would animate a standing player.
		/// </para>
		/// </summary>
		public Vector3 LocomotionVelocity
			=> body ? body.linearVelocity + _headVelocity : Vector3.zero;

		/// <summary>
		/// Pushes a locomotion value to the listeners, only when it changed: nothing is polled every frame.
		/// </summary>
		private void Announce(ref Vector3 stored, Vector3 velocity) {
			if ((velocity - stored).sqrMagnitude < MIN_NOTIFY_VELOCITY * MIN_NOTIFY_VELOCITY)
				return;

			stored = velocity;
			OnMove.Invoke();
		}

		/// <summary>Announces the velocity of the body, without its vertical part (jumping is not a move).</summary>
		private void NotifyMoveChanged() {
			var velocity = body.linearVelocity;
			velocity.y = 0f;

			Announce(ref _notifiedVelocity, velocity);
		}

		/// <summary>Announces the velocity the head gives the body, only when it changed.</summary>
		private void SetHeadVelocity(Vector3 velocity)
			=> Announce(ref _headVelocity, velocity);

		protected virtual void UpdateTrackedObjects() {
			var count = GetFollowedParts(_followed);

			for (var i = 0; i < count; i++)
				_followStarts[i] = _followed[i].transform.position;

			_targetTrackedPos += transform.position - _lastUpdatePosition;

			var targetContainerPos = _targetTrackedPos + Vector3.up * heightOffset;

			if (_isGrounded)
				targetContainerPos.y = Mathf.MoveTowards(
					trackingContainer.position.y, targetContainerPos.y,
					(Mathf.Abs(trackingContainer.position.y - targetContainerPos.y) + 0.1f) * Time.deltaTime * heightSmoothSpeed);

			trackingContainer.position = targetContainerPos;

			for (var i = 0; i < count; i++)
				FollowPart(_followed[i], _followStarts[i]);

			_lastUpdatePosition = transform.position;
		}

		/// <summary>
		/// Parts asking for a follow behaviour (see <see cref="Part.CanFollow"/>), read from the parts registry:
		/// a foot or a tracker part that opts in is handled without the callers knowing about it.
		/// </summary>
		/// <returns>How many parts were written, never more than <paramref name="parts"/>.</returns>
		protected virtual int GetFollowedParts(Part[] parts) {
			var count = 0;

			foreach (var part in _parts.Values) {
				if (!part || !part.PartEnabled || !part.CanFollow)
					continue;

				if (count >= parts.Length)
					break;

				parts[count++] = part;
			}

			return count;
		}

		/// <summary>
		/// Puts a part back on the pose it had before the tracking space moved: a part pushing something stays put
		/// in the world, any other one runs its own stay-out-of-geometry rule.
		/// </summary>
		private void FollowPart(Part part, Vector3 start)
			=> part.FollowTrackingSpace(start);

		/// <summary>
		/// Moves the body under the head, so the feet stay below the view when the player walks in the room.
		/// <para>
		/// Only the horizontal offset is corrected (the vertical one is the head height, handled by the
		/// grounding). The move is swept against the layers the player collides with, so a wall stops the body
		/// even when the head keeps going, and it is <b>removed</b> from <c>_lastUpdatePosition</c>: the tracking
		/// space must not follow this catch-up, otherwise the head would move with the body and the two would
		/// never meet.
		/// </para>
		/// </summary>
		protected virtual void SyncBodyHead()
			=> SetHeadVelocity(ApplyBodyHeadMove() / Time.fixedDeltaTime);

		/// <summary>
		/// Moves the body under the head and returns the displacement that was applied, in world units.
		/// </summary>
		private Vector3 ApplyBodyHeadMove() {
			if (!Head || !Head.PartEnabled || !floorCollider)
				return Vector3.zero;

			var view = Head.View;
			transform.rotation = Quaternion.Euler(0f, view ? view.eulerAngles.y : transform.eulerAngles.y, 0f);

			var position = body.position;
			var delta = Head.Position - position;
			delta.y = 0f;

			var distance = delta.magnitude;
			if (distance <= 0.001f)
				return Vector3.zero;

			var direction = delta / distance;
			var radius = BodyRadius;

			var origin = new Vector3(position.x, position.y + radius, position.z);

			if (Physics.SphereCast(origin, radius, direction, out var hit, distance, _handPlayerMask, QueryTriggerInteraction.Ignore))
				distance = Mathf.Max(0f, hit.distance - Physics.defaultContactOffset);

			if (distance <= 0f)
				return Vector3.zero;

			var move = direction * distance;

			body.position = position + move;
			_lastUpdatePosition += move;

			return move;
		}

		/// <summary>
		/// Crouches the player when a part has a ceiling right above it.
		/// <para>
		/// Nothing is pushed here: the parts are bodies with their own colliders, so the world holds them by itself. Only the
		/// crouch is ours, since it folds the player and not a collider.
		/// </para>
		/// </summary>
		protected virtual void ResolveEnvironmentCollisions() {
			if (!useEnvironmentCollision)
				return;

			var crouch = false;

			foreach (var part in _parts.Values) {
				if (!part)
					continue;

				if (part.PhysicsFollow || !part.PartEnabled || !part.CollideWithEnvironment) {
					part.IsUnderLowCeiling = false;
					continue;
				}

				part.IsUnderLowCeiling = TryGetLowCeiling(part, out _);

				if (part.IsUnderLowCeiling)
					crouch = true;
			}

			UpdateAutoCrouch(crouch);
		}

		private int EnvironmentMask(Part part)
			=> part is Hand ? handPartMask : _handPlayerMask;

		/// <summary>Whether the free space above a part is lower than its standing clearance.</summary>
		private bool TryGetLowCeiling(Part part, out RaycastHit hit) {
			hit = default;

			if (!part.CrouchUnderCeiling || part.StandingClearance <= 0f)
				return false;

			if (part.RawPosition.y - transform.position.y <= crouchHeight * 0.5f)
				return false;

			var radius = Mathf.Max(part.CollisionRadius, Physics.defaultContactOffset);
			return Physics.SphereCast(
				part.RawPosition, radius, Vector3.up,
				out hit, part.StandingClearance,
				EnvironmentMask(part), QueryTriggerInteraction.Ignore);
		}

		/// <summary>Latches the automatic crouch while something squeezes the player.</summary>
		private void UpdateAutoCrouch(bool squeezed) {
			if (!autoCrouch) {
				_autoCrouching = false;
				return;
			}

			if (squeezed) {
				_autoCrouching = true;
				_lastAutoCrouchTime = Time.time;
				return;
			}

			if (_autoCrouching && Time.time - _lastAutoCrouchTime >= autoCrouchReleaseDelay)
				_autoCrouching = false;
		}

		protected virtual bool CanInputMove()
			=> true;

		protected virtual void UpdateTurn(float deltaTime) {
			if (Mathf.Abs(_turningAxis) <= turnDeadzone)
				return;

			var snap = rotationType == PlayerRotationType.Snap;

			if (snap && !_axisReset)
				return;

			var angle = snap
				? _turningAxis > 0f ? snapTurnAngle : -snapTurnAngle
				: smoothTurnSpeed * Mathf.MoveTowards(_turningAxis, 0f, turnDeadzone) * deltaTime;

			var count = GetFollowedParts(_followed);

			if (snap) {
				var recenter = transform.position - Head.Position;
				recenter.y = 0f;
				trackingContainer.position += recenter;
			}

			for (var i = 0; i < count; i++)
				_followStarts[i] = _followed[i].transform.position;

			_lastUpdatePosition = new Vector3(transform.position.x, _lastUpdatePosition.y, transform.position.z);

			trackingContainer.RotateAround(transform.position, Vector3.up, angle);
			_targetTrackedPos = new Vector3(trackingContainer.position.x, _targetTrackedPos.y, trackingContainer.position.z);

			for (var i = 0; i < count; i++)
				_followed[i].FollowTurn();

			if (snap)
				for (var i = 0; i < count; i++)
					_followed[i].FollowTrackingSpace(_followStarts[i]);

			Physics.SyncTransforms();

			NotifyTurn(angle);

			if (snap) {
				OnSnapTurn?.Invoke(this);
				_axisReset = false;
			} else
				OnSmoothTurn?.Invoke(this);

			if (Mathf.Abs(_turningAxis) < turnResetzone)
				_axisReset = true;
		}

		private void NotifyTurn(float angle) {
			foreach (var part in _parts.Values)
				part.OnPlayerTurn(angle);
		}

		protected virtual Vector3 AlterDirection(Vector3 moveAxis) {
			var forward = ForwardFollow;
			if (!forward)
				return moveAxis;

			return useGrounding
				? Quaternion.AngleAxis(forward.eulerAngles.y, Vector3.up) * moveAxis
				: forward.rotation * moveAxis;
		}
	}
}
