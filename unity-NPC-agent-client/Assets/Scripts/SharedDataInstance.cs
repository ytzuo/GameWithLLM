using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

public class SharedDataInstance : Singleton<SharedDataInstance>
{
    public List<LlmMessage> messageList;
    
    protected override void Init()
    {
        base.Init();
        DontDestroyOnLoad(gameObject);
    }
}
