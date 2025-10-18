using System.Collections;
using System.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace BrgRenderSystem.Tests
{
    public class RendererTest : IPrebuildSetup, IPostBuildCleanup
    {
        private const string k_SceneName = "Packages/com.anyg.brg-rendersystem/Tests/Test.unity";
        private string lastScene;
        private float lastLodBias = 1;
        private int lastVSyncCount = 0;
        private int lastTargetFrameRate = 60;
        
        [UnitySetUp]
        public IEnumerator BeforeTest()
        {
            lastLodBias = QualitySettings.lodBias;
            lastVSyncCount = QualitySettings.vSyncCount;
            lastTargetFrameRate = Application.targetFrameRate;
            // lastScene = SceneManager.GetActiveScene().path;
            QualitySettings.lodBias = 1;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            SceneManager.LoadScene(k_SceneName);
            yield return null;
        }
        
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            QualitySettings.lodBias = lastLodBias;
            QualitySettings.vSyncCount = lastVSyncCount;
            Application.targetFrameRate = lastTargetFrameRate;
            // SceneManager.LoadScene(lastScene);
            yield return null;
        }
        
        // Ensure that render content are correctly set in the renderer group.
        [UnityTest]
        public IEnumerator RendererGroupContentTest()
        {
            const int testArraySize = 96;
            GameObject rootGameObject = new GameObject("root");
            GameObject instance = SceneManager.GetActiveScene().GetRootGameObjects().First(g => g.name == "TestSource");
            GameObject[,] testObjects = new GameObject[testArraySize, testArraySize];
            
            // 96*96=9216, large enough to test the performance and stability.
            Vector3 leftBottom = new Vector3(-testArraySize * 0.5f + 0.5f, 1, -testArraySize * 0.5f + 0.5f);
            for (int i = 0; i < testArraySize; ++i)
                for (int j = 0; j < testArraySize; ++j)
                    testObjects[i, j] = Object.Instantiate(instance, (leftBottom + new Vector3(i, 0, j)) * 1.5f,
                        Quaternion.identity, rootGameObject.transform);
            
            yield return null;
            // EditorApplication.isPaused = true;
            yield return null;

            // add and remove objects sequence to test the performance and stability.
            for (int k = 0; k < 5; ++k)
            {
                for (int i = 0; i < testArraySize; ++i)
                {
                    for (int j = 0; j < testArraySize; ++j)
                    {
                        testObjects[i, j].SetActive(!testObjects[i, j].activeSelf);
                        if (j % (testArraySize / 4) == 0)
                            yield return null;
                    }
                }
                
                yield return null;
                // EditorApplication.isPaused = true;
                yield return null;
            }
            
            // add and remove objects randomly to test the performance and stability.
            for (int i = 0; i < testArraySize * 4; ++i)
            {
                int xAxis = Random.Range(0, testArraySize - 1);
                int yAxis = Random.Range(0, testArraySize - 1);
                int size = Random.Range(1, 10);
                for (int x = xAxis; x < xAxis + size && x < testArraySize; ++x)
                {
                    for (int y = yAxis; y < yAxis + size && y < testArraySize; ++y)
                    {
                        testObjects[x, y].SetActive(!testObjects[x, y].activeSelf);
                    }
                }
                
                yield return null;
            }
            
            yield return null;
            // EditorApplication.isPaused = true;
            yield return null;
            
            // test the performance and stability of stable status.
            for (int k = 0; k < 5; ++k)
            {
                for (int i = 0; i < testArraySize; ++i)
                    for (int j = 0; j < testArraySize; ++j)
                        testObjects[i, j].SetActive(k % 2 == 0);
            
                for (int i = 0; i < testArraySize; ++i)
                    yield return null;
            }
        }
        
        public void Setup()
        {
#if UNITY_EDITOR
            if (EditorBuildSettings.scenes.Any(scene => scene.path == k_SceneName))
            {
                return;
            }
            
            var includedScenes = EditorBuildSettings.scenes.ToList();
            includedScenes.Add(new EditorBuildSettingsScene(k_SceneName, true));
            EditorBuildSettings.scenes = includedScenes.ToArray();
#endif
        }
        
        public void Cleanup()
        {
#if UNITY_EDITOR
            EditorBuildSettings.scenes = EditorBuildSettings.scenes.Where(scene => scene.path != k_SceneName).ToArray();
#endif
        }
    }
}
