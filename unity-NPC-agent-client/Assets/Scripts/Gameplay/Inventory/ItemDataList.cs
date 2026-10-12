using System.Collections.Generic;
using UnityEngine;


/// <summary>
/// 物品静态数据表
/// </summary>
[CreateAssetMenu(menuName = "Inventory/Item List", fileName = "New Item Data List")]
public class ItemDataList : ScriptableObject
{
    public List<ItemData> items;
}
