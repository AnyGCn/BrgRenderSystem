using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
// using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Assert = UnityEngine.Assertions.Assert;

namespace BrgRenderSystem.Tests
{
    public class RendererTest
    {
        public static readonly string packagePath = "Packages/com.anyg.brg-rendersystem";
        
        [SetUp]
        public void Setup()
        {
            SceneManager.LoadScene($"{packagePath}/Tests/Test.unity");
        }
        
        // Ensure that render content are correctly set in the renderer group.
        [UnityTest]
        public IEnumerator RendererGroupContentTest()
        {
            const int testArraySize = 20;
            Camera camera = Camera.main;
            GameObject rootGameObject = new GameObject("root");
            GameObject instance = SceneManager.GetActiveScene().GetRootGameObjects().First(g => g.name == "TestSource");
            for (int i = -testArraySize; i <= testArraySize; ++i)
            {
                for (int j = -testArraySize; j < testArraySize; ++j)
                {
                    GameObject go = Object.Instantiate(instance, new Vector3(i * 2, 1, j * 2), Quaternion.identity, rootGameObject.transform);
                    go.SetActive(true);
                    yield return null;
                }
            }
        }
    }
}
