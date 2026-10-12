using UnityEngine;

namespace GameWithLLM.NpcAnimation.Merchant
{
    public sealed class AnimationDriver : INpcAnimationDriver
    {
        private Animator animator;
        public void Bind(Animator value) { animator = value; }
        public void Tick(float deltaTime, float movementSpeed)
        {
            // The sample controller has an idle clip; alter playback with motion.
            if (animator != null) animator.speed = movementSpeed > 0.05f ? 1.5f : 1f;
        }
        public void Dispose() { if (animator != null) animator.speed = 1f; animator = null; }
    }
}
