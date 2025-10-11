using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
// using UnityEditor;
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
            const int testArraySize = 96;
            Camera camera = Camera.main;
            GameObject rootGameObject = new GameObject("root");
            GameObject instance = SceneManager.GetActiveScene().GetRootGameObjects().First(g => g.name == "TestSource");
            GameObject[,] testObjects = new GameObject[testArraySize, testArraySize];
            
            // 96*96=9216, large enough to test the performance and stability.
            int count = 0;
            for (int i = 0; i < testArraySize; ++i)
            {
                for (int j = 0; j < testArraySize; ++j)
                {
                    testObjects[i, j] = Object.Instantiate(instance, new Vector3(i * 2 - testArraySize + 1, 1, j * 2 - testArraySize + 1),
                        Quaternion.identity, rootGameObject.transform);
                    testObjects[i, j].SetActive(true);
                    count++;
                    if (count == 50)
                    {
                        count = 0;
                        yield return null;
                    }
                }
            }
            
            yield return null;
            // EditorApplication.isPaused = true;
            yield return null;

            for (int k = 0; k < 2; ++k)
            {
                for (int i = 0; i < testArraySize; ++i)
                {
                    for (int j = 0; j < testArraySize; ++j)
                    {
                        testObjects[i, j].SetActive(!testObjects[i, j].activeSelf);
                        count++;
                        if (count == 50)
                        {
                            count = 0;
                            yield return null;
                        }
                    }
                }
                
                yield return null;
                // EditorApplication.isPaused = true;
                yield return null;
            }
            
            // // add and remove some objects to test the performance and stability.
            // count = 200;
            // for (int i = 0; i < count; ++i)
            // {
            //     int xAxis = Random.Range(0, testArraySize - 1);
            //     int yAxis = Random.Range(0, testArraySize - 1);
            //     int size = Random.Range(1, 10);
            //     for (int x = xAxis; x < xAxis + size && x < testArraySize; ++x)
            //     {
            //         for (int y = yAxis; y < yAxis + size && y < testArraySize; ++y)
            //         {
            //             testObjects[x, y].SetActive(!testObjects[x, y].activeSelf);
            //         }
            //     }
            //     
            //     yield return new WaitForSeconds(0.1f);
            // }
            //
            // // test the performance and stability of stable status.
            // for (int i = 0; i < testArraySize; ++i)
            //     for (int j = 0; j < testArraySize; ++j)
            //         testObjects[i, j].SetActive(true);
            //
            // count = 200;
            // for (int i = 0; i < count; ++i)
            //     yield return null;
        }
    }
}
