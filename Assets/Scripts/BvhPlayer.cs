using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// BVH 骨骼动画播放器。
/// 将 walk1.bvh 放入 Assets/StreamingAssets，再把本脚本挂到场景中的空物体上。
/// </summary>
public class BvhPlayer : MonoBehaviour
{
    [Header("BVH 文件（位于 Assets/StreamingAssets）")]
    [SerializeField] private string bvhFileName = "walk1.bvh";

    [Header("统一缩放：OFFSET 与根节点平移都会乘以该值")]
    [SerializeField] private float unitScale = 0.01f;

    [Header("播放速度")]
    [SerializeField, Range(0.1f, 3f)] private float playbackSpeed = 1f;

    private sealed class BvhJoint
    {
        public string Name;
        public Vector3 Offset;
        public string[] Channels = new string[0];
        public int ChannelStart;
        public readonly List<BvhJoint> Children = new List<BvhJoint>();
        public bool HasEndSite;
        public Vector3 EndSiteOffset;
        public Transform Transform;
        public Transform EndSiteTransform;
    }

    private readonly List<BvhJoint> joints = new List<BvhJoint>();
    private BvhJoint rootJoint;
    private float[][] frames;
    private float frameTime;
    private float currentFrame;
    private bool isPlaying = true;
    private string loadError;

    private string[] tokens;
    private int tokenIndex;
    private Transform skeletonRoot;
    private Camera sceneCamera;
    private readonly Vector3 cameraOffset = new Vector3(3.5f, 1.8f, 4.5f);

    private void Start()
    {
        try
        {
            string path = Path.Combine(Application.streamingAssetsPath, bvhFileName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "未找到 BVH 文件。请把 walk1.bvh 放入 Assets/StreamingAssets。", path);
            }

            ParseBvh(File.ReadAllText(path));
            BuildSkeleton();
            ApplyFrame(0f);
            PlaceSkeletonAtOrigin();
            CreateGround();
            SetupCamera();

            Debug.Log(string.Format(
                "BVH 加载成功：{0} 个节点，{1} 帧，帧时间 {2:F6} 秒。",
                joints.Count, frames.Length, frameTime));
        }
        catch (Exception exception)
        {
            loadError = exception.Message;
            Debug.LogError("BVH 加载失败：" + exception);
        }
    }

    private void Update()
    {
        if (frames == null || frames.Length == 0)
        {
            return;
        }

        if (isPlaying && frames.Length > 1)
        {
            // 按 BVH 的 Frame Time 播放，而不是假设文件为 30 FPS。
            currentFrame += Time.deltaTime * playbackSpeed / frameTime;
            currentFrame = Mathf.Repeat(currentFrame, frames.Length - 1);
            ApplyFrame(currentFrame);
        }

        FollowCamera(false);
    }

    // ------------------------- BVH 解析 -------------------------

    private void ParseBvh(string text)
    {
        tokens = Regex.Split(text.Trim(), @"\s+");
        tokenIndex = 0;
        joints.Clear();

        Expect("HIERARCHY");
        rootJoint = ParseJoint();
        Expect("MOTION");
        Expect("Frames:");

        int frameCount = int.Parse(Next(), CultureInfo.InvariantCulture);
        Expect("Frame");
        Expect("Time:");
        frameTime = ParseFloat(Next());

        int channelCount = 0;
        foreach (BvhJoint joint in joints)
        {
            joint.ChannelStart = channelCount;
            channelCount += joint.Channels.Length;
        }

        if (frameCount <= 0 || frameTime <= 0f || channelCount == 0)
        {
            throw new FormatException("BVH 的帧数、帧时间或通道数无效。");
        }

        frames = new float[frameCount][];
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            float[] frame = new float[channelCount];
            for (int channelIndex = 0; channelIndex < channelCount; channelIndex++)
            {
                if (tokenIndex >= tokens.Length)
                {
                    throw new FormatException("MOTION 数据不足，文件可能不完整。");
                }

                frame[channelIndex] = ParseFloat(Next());
            }

            frames[frameIndex] = frame;
        }
    }

    private BvhJoint ParseJoint()
    {
        string kind = Next(); // ROOT 或 JOINT
        if (kind != "ROOT" && kind != "JOINT")
        {
            throw new FormatException("应为 ROOT/JOINT，实际为：" + kind);
        }

        BvhJoint joint = new BvhJoint { Name = Next() };
        joints.Add(joint);
        Expect("{");

        while (true)
        {
            string token = Next();
            switch (token)
            {
                case "OFFSET":
                    joint.Offset = ReadVector3();
                    break;

                case "CHANNELS":
                    int count = int.Parse(Next(), CultureInfo.InvariantCulture);
                    joint.Channels = new string[count];
                    for (int i = 0; i < count; i++)
                    {
                        joint.Channels[i] = Next();
                    }
                    break;

                case "JOINT":
                    tokenIndex--; // 交给递归函数读取 JOINT 和子节点名称
                    joint.Children.Add(ParseJoint());
                    break;

                case "End":
                    Expect("Site");
                    Expect("{");
                    Expect("OFFSET");
                    joint.EndSiteOffset = ReadVector3();
                    joint.HasEndSite = true;
                    Expect("}");
                    break;

                case "}":
                    return joint;

                default:
                    throw new FormatException(
                        "节点 " + joint.Name + " 中出现未知标记：" + token);
            }
        }
    }

    private string Next()
    {
        if (tokenIndex >= tokens.Length)
        {
            throw new FormatException("BVH 文件意外结束。");
        }

        return tokens[tokenIndex++];
    }

    private void Expect(string expected)
    {
        string actual = Next();
        if (actual != expected)
        {
            throw new FormatException(
                "应为 " + expected + "，实际为 " + actual + "。");
        }
    }

    private Vector3 ReadVector3()
    {
        return new Vector3(ParseFloat(Next()), ParseFloat(Next()), ParseFloat(Next()));
    }

    private static float ParseFloat(string value)
    {
        return float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // ------------------------- 骨骼创建与动画 -------------------------

    private void BuildSkeleton()
    {
        skeletonRoot = new GameObject("BVH Skeleton").transform;
        skeletonRoot.SetParent(transform, false);

        Material boneMaterial = CreateMaterial(new Color(0.95f, 0.65f, 0.15f));
        Material jointMaterial = CreateMaterial(new Color(0.1f, 0.65f, 1f));
        BuildJointRecursive(rootJoint, skeletonRoot, boneMaterial, jointMaterial);
    }

    private void BuildJointRecursive(
        BvhJoint joint,
        Transform parent,
        Material boneMaterial,
        Material jointMaterial)
    {
        GameObject jointObject = new GameObject(joint.Name);
        joint.Transform = jointObject.transform;
        joint.Transform.SetParent(parent, false);
        joint.Transform.localPosition = joint.Offset * unitScale;
        CreateSphere(joint.Transform, 0.045f, jointMaterial);

        foreach (BvhJoint child in joint.Children)
        {
            BuildJointRecursive(child, joint.Transform, boneMaterial, jointMaterial);
            CreateBone(joint.Transform, child.Offset * unitScale, boneMaterial);
        }

        if (joint.HasEndSite)
        {
            GameObject endObject = new GameObject(joint.Name + "_EndSite");
            joint.EndSiteTransform = endObject.transform;
            joint.EndSiteTransform.SetParent(joint.Transform, false);
            joint.EndSiteTransform.localPosition = joint.EndSiteOffset * unitScale;
            CreateBone(joint.Transform, joint.EndSiteOffset * unitScale, boneMaterial);
        }
    }

    private static void CreateBone(Transform parent, Vector3 localEnd, Material material)
    {
        float length = localEnd.magnitude;
        if (length < 0.00001f)
        {
            return;
        }

        GameObject bone = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        bone.name = "Bone";
        RemoveCollider(bone);
        bone.GetComponent<Renderer>().sharedMaterial = material;
        bone.transform.SetParent(parent, false);
        bone.transform.localPosition = localEnd * 0.5f;
        bone.transform.localRotation = Quaternion.FromToRotation(Vector3.up, localEnd / length);

        // Unity 圆柱体默认高度为 2，因此 Y 缩放为 length / 2。
        bone.transform.localScale = new Vector3(0.025f, length * 0.5f, 0.025f);
    }

    private static void CreateSphere(Transform parent, float diameter, Material material)
    {
        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "Joint";
        RemoveCollider(sphere);
        sphere.GetComponent<Renderer>().sharedMaterial = material;
        sphere.transform.SetParent(parent, false);
        sphere.transform.localPosition = Vector3.zero;
        sphere.transform.localScale = Vector3.one * diameter;
    }

    private static void RemoveCollider(GameObject primitive)
    {
        Collider primitiveCollider = primitive.GetComponent<Collider>();
        if (primitiveCollider != null)
        {
            Destroy(primitiveCollider);
        }
    }

    private void ApplyFrame(float framePosition)
    {
        int frameA = Mathf.Clamp(Mathf.FloorToInt(framePosition), 0, frames.Length - 1);
        int frameB = Mathf.Min(frameA + 1, frames.Length - 1);
        float blend = Mathf.Clamp01(framePosition - frameA);

        foreach (BvhJoint joint in joints)
        {
            Vector3 positionA;
            Quaternion rotationA;
            EvaluateJoint(joint, frames[frameA], out positionA, out rotationA);

            if (frameA == frameB || blend <= 0f)
            {
                joint.Transform.localPosition = positionA;
                joint.Transform.localRotation = rotationA;
                continue;
            }

            Vector3 positionB;
            Quaternion rotationB;
            EvaluateJoint(joint, frames[frameB], out positionB, out rotationB);
            joint.Transform.localPosition = Vector3.Lerp(positionA, positionB, blend);
            joint.Transform.localRotation = Quaternion.Slerp(rotationA, rotationB, blend);
        }
    }

    private void EvaluateJoint(
        BvhJoint joint,
        float[] frame,
        out Vector3 position,
        out Quaternion rotation)
    {
        // OFFSET 和平移通道使用同一个缩放系数，避免骨架缩小但位移未缩小。
        position = joint.Offset * unitScale;
        rotation = Quaternion.identity;

        for (int i = 0; i < joint.Channels.Length; i++)
        {
            string channel = joint.Channels[i];
            float value = frame[joint.ChannelStart + i];

            switch (channel)
            {
                case "Xposition": position.x += value * unitScale; break;
                case "Yposition": position.y += value * unitScale; break;
                case "Zposition": position.z += value * unitScale; break;

                // 必须按照文件中 CHANNELS 的声明顺序逐个相乘。
                case "Xrotation":
                    rotation *= Quaternion.AngleAxis(value, Vector3.right);
                    break;
                case "Yrotation":
                    rotation *= Quaternion.AngleAxis(value, Vector3.up);
                    break;
                case "Zrotation":
                    rotation *= Quaternion.AngleAxis(value, Vector3.forward);
                    break;
                default:
                    throw new FormatException("不支持的通道：" + channel);
            }
        }
    }

    // ------------------------- 场景显示 -------------------------

    private void PlaceSkeletonAtOrigin()
    {
        Vector3 rootPosition = rootJoint.Transform.position;
        skeletonRoot.position -= new Vector3(rootPosition.x, 0f, rootPosition.z);

        // 只消除首帧的恒定地面偏差，不改变后续帧的上下运动。
        float minimumY = float.MaxValue;
        foreach (BvhJoint joint in joints)
        {
            minimumY = Mathf.Min(minimumY, joint.Transform.position.y);
            if (joint.EndSiteTransform != null)
            {
                minimumY = Mathf.Min(minimumY, joint.EndSiteTransform.position.y);
            }
        }

        skeletonRoot.position -= new Vector3(0f, minimumY, 0f);
    }

    private void CreateGround()
    {
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "BVH Ground";
        ground.transform.position = Vector3.zero;
        ground.transform.localScale = new Vector3(20f, 1f, 20f);
        ground.GetComponent<Renderer>().sharedMaterial =
            CreateMaterial(new Color(0.18f, 0.2f, 0.23f));
    }

    private void SetupCamera()
    {
        sceneCamera = Camera.main;
        if (sceneCamera == null)
        {
            GameObject cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            sceneCamera = cameraObject.AddComponent<Camera>();
        }

        sceneCamera.fieldOfView = 50f;
        FollowCamera(true);
    }

    private void FollowCamera(bool immediately)
    {
        if (sceneCamera == null || rootJoint == null || rootJoint.Transform == null)
        {
            return;
        }

        Vector3 lookTarget = rootJoint.Transform.position + Vector3.up * 0.2f;
        Vector3 desiredPosition = lookTarget + cameraOffset;
        sceneCamera.transform.position = immediately
            ? desiredPosition
            : Vector3.Lerp(sceneCamera.transform.position, desiredPosition,
                1f - Mathf.Exp(-8f * Time.deltaTime));
        sceneCamera.transform.LookAt(lookTarget);
    }

    private static Material CreateMaterial(Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Unlit/Color");

        Material material = new Material(shader);
        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }
        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", color);
        }
        return material;
    }

    // ------------------------- 播放控制界面 -------------------------

    private void OnGUI()
    {
        if (!string.IsNullOrEmpty(loadError))
        {
            GUI.Box(new Rect(10f, 10f, 620f, 80f), "BVH 加载失败");
            GUI.Label(new Rect(25f, 45f, 590f, 45f), loadError);
            return;
        }

        if (frames == null)
        {
            return;
        }

        GUILayout.BeginArea(new Rect(10f, 10f, 370f, 190f), GUI.skin.box);
        GUILayout.Label("BVH 文件：" + bvhFileName);
        GUILayout.Label(string.Format(
            "节点：{0}    总帧数：{1}    帧率：{2:F1} FPS",
            joints.Count, frames.Length, 1f / frameTime));

        GUILayout.BeginHorizontal();
        if (GUILayout.Button(isPlaying ? "暂停" : "播放", GUILayout.Height(30f)))
        {
            isPlaying = !isPlaying;
        }
        if (GUILayout.Button("回到起点", GUILayout.Height(30f)))
        {
            currentFrame = 0f;
            ApplyFrame(0f);
            FollowCamera(true);
        }
        GUILayout.EndHorizontal();

        GUILayout.Label("播放速度：" + playbackSpeed.ToString("0.00") + " x");
        playbackSpeed = GUILayout.HorizontalSlider(playbackSpeed, 0.1f, 3f);

        int displayFrame = Mathf.Clamp(Mathf.FloorToInt(currentFrame) + 1, 1, frames.Length);
        GUILayout.Label(string.Format(
            "当前帧：{0}    时间：{1:F2} 秒", displayFrame, currentFrame * frameTime));
        GUILayout.Label("点击按钮播放/暂停；拖动滑块调节速度");
        GUILayout.EndArea();
    }
}
