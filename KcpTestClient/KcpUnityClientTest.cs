// using UnityEngine;
// using System;
// using System.Net;
// using System.Threading.Tasks;
// using KcpSharp;
//
// public class KcpUnityClientTest : MonoBehaviour
// {
//     // Start is called once before the first execution of Update after the MonoBehaviour is created
//     void Start()
//     {
//         
//     }
//
//     // Update is called once per frame
//     void Update()
//     {
//         
//     }
//
//     public void OnClick()
//     {
//         var remote = new IPEndPoint(IPAddress.Loopback, 12345);
//         RunClientAsync(remote);
//     }
//
//     static async Task RunClientAsync(IPEndPoint remote)
//     {
//         var connection = await KcpConnection.Connect(remote);
//         Console.WriteLine($"kcp Connected to {remote}, conv: {connection.Conv}");
//         connection.OnReceive = (kcpConnection, bytes) =>
//         {
//             Debug.Log($"On Client received: {bytes.Length}");
//         };
//
//         var index = 0;
//         while (true)
//         {
//             connection.SendAsync(System.Text.Encoding.UTF8.GetBytes($"hello {++index}"));
//             if (index >= 5)
//             {
//                 await connection.CloseAsync();
//                 Debug.Log("成功关闭");
//                 break;
//             }
//             
//             await Task.Delay(1000);
//         }
//     }
// }
