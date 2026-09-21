using UnityEngine;

public class Singleton<T> : MonoBehaviour where T : Singleton<T> 
{
    private static T instance;

    public static T Instance
    {
        get
        {
            if (instance != null) return instance;

            instance = FindFirstObjectByType<T>();
            if (instance == null)
            {
                new GameObject("Singleton of " + typeof(T)).AddComponent<T>();
            }
            else instance.Init();

            return instance;

        }
    }

    private void Awake()
    {
        instance = this as T;
        Init();
    }
    protected virtual void Init()
    {

    }
}
