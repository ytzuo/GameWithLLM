using GameWithLLM.AgentRuntime;
using UnityEngine;

namespace GameWithLLM.AgentRuntime.Samples.SwitchDemo
{
    public sealed class SwitchAgentEntity : MonoBehaviour, IGameObjectAgentEntity
    {
        [SerializeField] private string entityId = "switch:loading-bay";
        [SerializeField] private bool isOn;
        public string EntityId => entityId;
        public bool IsOnline => isActiveAndEnabled;
        public GameObject GameObject => gameObject;
        public bool IsOn => isOn;
        public void SetState(bool value) => isOn = value;
    }
}
