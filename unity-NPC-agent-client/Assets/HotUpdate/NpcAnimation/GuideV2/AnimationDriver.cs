using UnityEngine;

namespace GameWithLLM.NpcAnimation.Guide
{
    public sealed class AnimationDriver : INpcAnimationDriver
    {
        private Animator animator;
        public void Bind(Animator value) { animator = value; }
        public void Tick(float deltaTime, float movementSpeed)
        {
            if (animator != null) animator.speed = movementSpeed > 0.05f ? 2f : 1f;
        }
        public void Dispose() { if (animator != null) animator.speed = 1f; animator = null; }
    }
}
