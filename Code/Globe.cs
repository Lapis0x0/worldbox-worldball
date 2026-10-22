using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace WorldboxGlobe
{
    // 地球仪视图：星球世界里，相机拉过原版的最远还能接着拉，地图逐渐卷成一个球；再往外是完整的球，拖动是转球。
    // 原版照常把整张地图画出来，只是画进一张贴图（拍摄相机正对整张地图），这张贴图再卷到球上，
    // 用一台单独的相机画在原版画面之上。原版画了什么球上就有什么，主相机这时不用再画一遍。
    // 军队、国王这类随缩放变大的标记不拍进贴图，而是立在球面对应的位置上，免得贴在球面上被压扁；
    // 名牌按球面的位置摆，鼠标的位置反算回地图上的格子，点选、神力、悬停提示照原版用。
    //
    // 投影：星球世界的地图横向是经度、纵向是纬度，直接按经纬度卷回球上。弯曲程度跟着缩放走：
    // 球的半径从无穷大缩到地图宽 / 2π，刚开始弯时网格和平面地图重合，换过去看不出来。
    // 球的朝向就是原版相机的位置：转球改的是相机位置，横向转一圈回到原处，拉近后落在转到的地方
    public static class Globe
    {
        const float BendStart = 1f;     // 从原版最远的几倍开始弯
        const float BendEnd = 2f;       // 到几倍弯成完整的球
        const float ZoomMax = 2.6f;     // 最远能拉到原版最远的几倍
        const float Fill = 0.6f;        // 刚弯成球时，球的直径占屏幕高的比例
        const float Shade = 0.45f;      // 球边缘比正面暗多少
        const int Cols = 192;
        const int Rows = 96;
        const int Segments = 96;
        const float Halo = 0.07f;       // 大气的厚度，以球的半径为 1
        static readonly Color Atmosphere = new Color(0.45f, 0.7f, 1f, 0.45f);
        const int StarSize = 512;       // 星空贴图的边长，一个像素对应屏幕上一个像素
        const float StarDrift = 0.15f;  // 横向转一圈，星空移动贴图的几分之一
        const float SpaceDrift = 0.04f; // 太空背景图随转球移动的幅度（以屏幕大小为 1）
        const int PixelsPerTile = 4;    // 拍摄贴图里一格地块占几个像素
        const int MaxTexture = 4096;    // 拍摄贴图最宽多少像素，大地图就降低每格的像素
        const int LightPixelsPerTile = 2;   // 灯光贴图一格几个像素：灯光是大片的光斑，用不着地图那么细
        const int LightMaxTexture = 2048;
        const float MarkerMargin = 0.5f; // 标记离地图多远（以地图高为 1）就不画：鼠标移到球外时，原版会把光标处的标记放到很远的地方
        const int Layer = 31;           // 只有地球仪相机画这一层
        const string RootName = "globe_view";
        static readonly Color Space = new Color(0.02f, 0.03f, 0.06f);

        static GlobeView _view;

        // 地图在弯（或已经是球）：原版的鼠标位置、名牌、拖动交给地球仪
        public static bool Active => _view != null && _view.Bend > 0f;

        public static void Init()
        {
            // 热重载后旧版本建的对象还在，拆掉重建
            GameObject old = GameObject.Find(RootName);
            if (old != null) UnityEngine.Object.Destroy(old);
            var root = new GameObject(RootName);
            UnityEngine.Object.DontDestroyOnLoad(root);
            _view = root.AddComponent<GlobeView>();
        }

        public static void Stop()
        {
            if (_view != null) UnityEngine.Object.Destroy(_view.gameObject);
            _view = null;
        }

        // 原版最远：地图长边的 1.1 倍（MoveCamera.resetZoom）
        static float VanillaMax() => (int)(Mathf.Max(MapBox.width, MapBox.height) * 1.1f);

        public class GlobeView : MonoBehaviour
        {
            public float Bend;      // 0 平面，1 完整的球

            Camera _camera;
            Camera _capture;        // 拍摄相机：正对整张地图，把原版的画面画进 _texture
            RenderTexture _texture;
            Camera _lightCapture;   // 灯光拍摄相机：照原版特效相机（黑暗时代的城市灯光）的设置拍整张地图
            RenderTexture _lightTexture;
            MeshRenderer _lights;   // 球面上的灯光，叠在地图上面
            RawImage _overlay;      // 原版的灯光叠层：按主相机的画面铺满屏幕，地球仪显示时藏起来
            bool _overlayHidden;
            int _mainMask = -1;     // 地球仪显示时主相机什么都不画，这里记着它原来画哪些层
            Mesh _mesh;
            Vector3[] _verts;
            Color32[] _colors;
            readonly List<int> _tris = new List<int>();
            MeshRenderer _surface;  // 球面：贴着拍下来的地图
            WorldLayer _terrain;
            MeshRenderer _halo;     // 大气：刚开始弯时铺满屏幕（原版地图外面的那片蓝），随缩放收拢成球外一圈光
            Mesh _haloMesh;
            Vector3[] _haloVerts;
            Color32[] _haloColors;
            MeshRenderer _stars;    // 找不到原版太空图时用的随机星点
            SpriteRenderer _space;  // 太空背景：原版加载界面的背景图，贴在相机前面，随转球微微移动
            Mesh _starMesh;
            readonly List<(SpriteRenderer from, SpriteRenderer to)> _eras = new List<(SpriteRenderer, SpriteRenderer)>();
            readonly List<SpriteRenderer> _erasHidden = new List<SpriteRenderer>();
            // 立到球面上的标记原来的位置和层。地球仪相机画完就放回去：原版按缓存的位置判断要不要重设，不能留着改过的
            readonly List<(Transform transform, Vector3 local)> _moved = new List<(Transform, Vector3)>();
            readonly List<(GameObject go, int layer)> _relayered = new List<(GameObject, int)>();
            Rect _map;              // 地图在世界坐标里的范围，取地形层的边界
            float _size;            // 地球仪相机的视野半高
            double _cx, _cy, _radius, _midY, _cosT, _sinT;   // 这一帧的投影：相机对着的位置、球的半径、绕横轴转的角度
            bool _dragging;
            int _touches;           // 上一帧按着几根手指，0 是鼠标
            Vector3 _lastMouse;

            void OnEnable()
            {
                Camera.onPreCull += PreCull;
                Camera.onPostRender += PostRender;
            }

            void OnDisable()
            {
                Camera.onPreCull -= PreCull;
                Camera.onPostRender -= PostRender;
            }

            void LateUpdate()
            {
                if (!Guard.On) { Hide(); return; }
                try { Step(); }
                catch (Exception e) { Hide(); Guard.Fail("地球仪", e); }
            }

            void OnDestroy()
            {
                Hide();
                Release(ref _texture);
                Release(ref _lightTexture);
            }

            static void Release(ref RenderTexture pTexture)
            {
                if (pTexture == null) return;
                pTexture.Release();
                Destroy(pTexture);
                pTexture = null;
            }

            void Step()
            {
                PutBackMarkers();
                MoveCamera move = MoveCamera.instance;
                if (World.world == null || move == null || MapBox.width <= 0 || !Planet.IsPlanet()) { Bend = 0f; Hide(); return; }
                float vanilla = VanillaMax();
                // 原版换窗口大小、换地图时会把上限改回去
                if (move.orthographic_size_max < vanilla * ZoomMax) move.orthographic_size_max = vanilla * ZoomMax;

                float zoom = move.main_camera.orthographicSize;
                float t = Mathf.Clamp01((zoom - vanilla * BendStart) / (vanilla * (BendEnd - BendStart)));
                Bend = t * t * (3f - 2f * t);
                if (Bend <= 0.001f) { Bend = 0f; Hide(); return; }

                if (_camera == null) Build();
                if (!Sync()) { Hide(); return; }

                // 球的半径：地图宽正好绕一圈时是 W / 2π，之前按 Bend 反比放大，Bend → 0 时趋于平面
                double edge = _map.width / 2.0;
                double radius = _map.width / (2.0 * Math.PI * Bend);

                // 视野：地图卷起来后横向变窄（球的直径只有地图宽的 1/π），得放大才看得清。放大倍数按「地图在屏幕上占多大」定：
                // 刚开始弯时和平面一样，弯成球时球的直径占屏幕高的 Fill，之后跟着缩放等比缩小
                double angle = edge / radius;
                double extent = angle < Math.PI / 2 ? radius * Math.Sin(angle) : radius;
                double boost = Mathf.Lerp(1f, (float)(Fill * vanilla * BendEnd / edge), Bend);
                _size = (float)(zoom * (extent / edge) / boost);

                Vector3 center = move.transform.position;
                if (!_camera.enabled) Tooltip.hideTooltip(null, true, string.Empty);
                _camera.backgroundColor = Space;
                _camera.enabled = true;
                _camera.orthographicSize = _size;
                _camera.transform.position = new Vector3(center.x, center.y, -10f);
                _camera.farClipPlane = (float)(radius * 2.0 + 20.0);
                Capture(move.main_camera);
                Lights();
                Shape(center.x, center.y, radius, move.main_camera.backgroundColor);
                PlaceMarkers();
                UpdateStars(center);
                UpdateEras(zoom);
            }

            void Build()
            {
                var go = new GameObject("globe_camera");
                go.transform.SetParent(transform, false);
                _camera = go.AddComponent<Camera>();
                Camera main = MoveCamera.instance.main_camera;
                _camera.orthographic = true;
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.cullingMask = 1 << Layer;
                _camera.depth = main.depth + 0.5f;
                _camera.nearClipPlane = 0.1f;
                _camera.allowHDR = false;
                _camera.allowMSAA = false;
                foreach (Camera c in Camera.allCameras)
                    if (c != _camera) c.cullingMask &= ~(1 << Layer);

                var shot = new GameObject("globe_capture");
                shot.transform.SetParent(transform, false);
                _capture = shot.AddComponent<Camera>();
                _capture.orthographic = true;
                _capture.clearFlags = CameraClearFlags.SolidColor;
                _capture.depth = main.depth - 0.5f;
                _capture.allowHDR = false;
                _capture.allowMSAA = false;
                _capture.enabled = false;

                var lightShot = new GameObject("globe_light_capture");
                lightShot.transform.SetParent(transform, false);
                _lightCapture = lightShot.AddComponent<Camera>();
                _lightCapture.orthographic = true;
                _lightCapture.depth = main.depth - 0.5f;
                _lightCapture.allowHDR = false;
                _lightCapture.allowMSAA = false;
                _lightCapture.enabled = false;

                _mesh = new Mesh { name = "globe_map" };
                _mesh.MarkDynamic();
                _verts = new Vector3[(Cols + 1) * (Rows + 1)];
                _colors = new Color32[_verts.Length];
                var uv = new Vector2[_verts.Length];
                for (int i = 0; i <= Rows; i++)
                for (int j = 0; j <= Cols; j++)
                    uv[i * (Cols + 1) + j] = new Vector2((float)j / Cols, (float)i / Rows);
                _mesh.vertices = _verts;
                _mesh.uv = uv;
                _mesh.colors32 = _colors;

                _haloMesh = new Mesh { name = "globe_halo" };
                _haloMesh.MarkDynamic();
                // 中心一点、一圈不透明的内圈、一圈全透明的外圈：内圈以内是实心的，内外圈之间淡出
                _haloVerts = new Vector3[1 + Segments * 2];
                _haloColors = new Color32[1 + Segments * 2];
                var haloTris = new int[Segments * 9];
                for (int k = 0; k < Segments; k++)
                {
                    int i0 = 1 + k, i1 = 1 + (k + 1) % Segments, o0 = i0 + Segments, o1 = i1 + Segments;
                    haloTris[k * 9] = 0; haloTris[k * 9 + 1] = i1; haloTris[k * 9 + 2] = i0;
                    haloTris[k * 9 + 3] = i0; haloTris[k * 9 + 4] = i1; haloTris[k * 9 + 5] = o0;
                    haloTris[k * 9 + 6] = o0; haloTris[k * 9 + 7] = i1; haloTris[k * 9 + 8] = o1;
                }
                _haloMesh.vertices = _haloVerts;
                _haloMesh.colors32 = _haloColors;
                _haloMesh.uv = new Vector2[1 + Segments * 2];
                _haloMesh.triangles = haloTris;

                _starMesh = new Mesh { name = "globe_stars" };
                _starMesh.MarkDynamic();
                _starMesh.vertices = new Vector3[4];
                _starMesh.uv = new Vector2[4];
                _starMesh.colors32 = new Color32[4];
                _starMesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            }

            // 找到地形层，建好球面、大气、太空和时代滤镜的渲染器，量出地图范围，备好拍摄贴图
            bool Sync()
            {
                if (_terrain == null)
                {
                    _terrain = FindObjectOfType<WorldLayer>();
                    if (_terrain == null || _terrain.sprRnd == null) return false;
                }
                if (_surface == null)
                {
                    Material material = _terrain.sprRnd.sharedMaterial;
                    _surface = NewRenderer("globe_surface", _mesh, material);
                    _surface.sharedMaterial.color = Color.white;
                    _halo = NewRenderer("globe_halo", _haloMesh, material);
                    _halo.sharedMaterial.mainTexture = Texture2D.whiteTexture;
                    _halo.sharedMaterial.color = Color.white;
                    _stars = NewRenderer("globe_stars", _starMesh, material);
                    _stars.transform.SetParent(_camera.transform, false);
                    _stars.sharedMaterial.mainTexture = StarTexture();
                    _stars.sharedMaterial.color = Color.white;
                    Sprite space = SpaceSprite();
                    if (space != null)
                    {
                        var bg = new GameObject("globe_space") { layer = Layer };
                        bg.transform.SetParent(_camera.transform, false);
                        _space = bg.AddComponent<SpriteRenderer>();
                        _space.sprite = space;
                    }
                    // 灯光：原版叠层用的材质照搬到球面上
                    _overlay = LightRenderer.instance != null ? LightRenderer.instance.GetComponent<RawImage>() : null;
                    if (_overlay != null) _lights = NewRenderer("globe_lights", _mesh, _overlay.material);
                    // 时代滤镜：原版贴在主相机前的几张全屏图，照样贴一份在地球仪相机前
                    if (WorldAgeEffects.instance != null)
                    {
                        foreach (SpriteRenderer from in WorldAgeEffects.instance.dict_effects.Values)
                        {
                            var go = new GameObject("globe_era_" + from.name) { layer = Layer };
                            go.transform.SetParent(_camera.transform, false);
                            SpriteRenderer to = go.AddComponent<SpriteRenderer>();
                            to.sharedMaterial = from.sharedMaterial;
                            _eras.Add((from, to));
                        }
                    }
                }

                SpriteRenderer terrain = _terrain.sprRnd;
                Bounds b = terrain.bounds;
                _map = new Rect(b.min.x, b.min.y, b.size.x, b.size.y);
                if (_map.width <= 0f || _map.height <= 0f) return false;
                Prepare(ref _texture, _capture, _surface, PixelsPerTile, MaxTexture);

                _surface.enabled = true;
                _surface.sortingLayerID = terrain.sortingLayerID;
                _surface.sortingOrder = terrain.sortingOrder;
                _halo.enabled = true;
                _halo.sortingLayerID = terrain.sortingLayerID;
                _halo.sortingOrder = terrain.sortingOrder - 1;
                _stars.enabled = _space == null;
                _stars.sortingLayerID = terrain.sortingLayerID;
                _stars.sortingOrder = terrain.sortingOrder - 2;
                if (_space != null)
                {
                    _space.enabled = true;
                    _space.sortingLayerID = terrain.sortingLayerID;
                    _space.sortingOrder = terrain.sortingOrder - 2;
                }
                return true;
            }

            // 拍摄贴图和地图一样的比例，换了地图大小就重建
            void Prepare(ref RenderTexture pTexture, Camera pCamera, Renderer pRenderer, int pPerTile, int pMax)
            {
                int tilesX = Mathf.Max(1, Mathf.RoundToInt(_map.width));
                int tilesY = Mathf.Max(1, Mathf.RoundToInt(_map.height));
                int k = Mathf.Clamp(pMax / tilesX, 1, pPerTile);
                int w = tilesX * k, h = tilesY * k;
                if (pTexture != null && pTexture.width == w && pTexture.height == h) return;
                pCamera.targetTexture = null;
                Release(ref pTexture);
                pTexture = new RenderTexture(w, h, 16, RenderTextureFormat.ARGB32)
                {
                    name = pCamera.name,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                pTexture.Create();
                pCamera.targetTexture = pTexture;
                pRenderer.sharedMaterial.mainTexture = pTexture;
            }

            // 拍摄相机照主相机的设置，正对整张地图；主相机的画面反正被地球仪盖住，这时不用画
            void Capture(Camera pMain)
            {
                if (_mainMask < 0) _mainMask = pMain.cullingMask;
                if (pMain.cullingMask != 0) pMain.cullingMask = 0;
                _capture.cullingMask = _mainMask & ~(1 << Layer);
                _capture.backgroundColor = pMain.backgroundColor;
                _capture.nearClipPlane = pMain.nearClipPlane;
                _capture.farClipPlane = pMain.farClipPlane;
                _capture.aspect = _map.width / _map.height;
                _capture.orthographicSize = _map.height / 2f;
                _capture.transform.position = new Vector3(_map.center.x, _map.center.y, pMain.transform.position.z);
                _capture.enabled = true;
            }

            // 黑暗时代的城市灯光：原版用特效相机把灯光拍成一张屏幕大小的图，叠在整个画面上。
            // 地球仪显示时照特效相机的设置把整张地图的灯光拍下来，贴到球面上，原版的叠层藏起来；颜色和浓淡照原版叠层
            void Lights()
            {
                if (_lights == null || _overlay == null) return;
                EffectsCamera effects = LightRenderer.instance != null ? LightRenderer.instance.effectsCamera : null;
                Camera fx = effects != null ? effects.GetComponent<Camera>() : null;
                bool on = fx != null && _overlay.gameObject.activeInHierarchy && (_overlay.enabled || _overlayHidden)
                    && _overlay.color.a > 0.001f;
                if (!on)
                {
                    HideLights();
                    return;
                }
                if (_overlay.enabled)
                {
                    _overlay.enabled = false;
                    _overlayHidden = true;
                }
                Prepare(ref _lightTexture, _lightCapture, _lights, LightPixelsPerTile, LightMaxTexture);
                _lightCapture.cullingMask = fx.cullingMask & ~(1 << Layer);
                _lightCapture.clearFlags = fx.clearFlags;
                _lightCapture.backgroundColor = fx.backgroundColor;
                _lightCapture.nearClipPlane = fx.nearClipPlane;
                _lightCapture.farClipPlane = fx.farClipPlane;
                _lightCapture.aspect = _map.width / _map.height;
                _lightCapture.orthographicSize = _map.height / 2f;
                _lightCapture.transform.position = new Vector3(_map.center.x, _map.center.y, fx.transform.position.z);
                _lightCapture.enabled = true;
                SpriteRenderer terrain = _terrain.sprRnd;
                _lights.sortingLayerID = terrain.sortingLayerID;
                _lights.sortingOrder = terrain.sortingOrder + 1;
                _lights.sharedMaterial.color = _overlay.color;
                _lights.enabled = true;
            }

            void HideLights()
            {
                if (_overlayHidden && _overlay != null) _overlay.enabled = true;
                _overlayHidden = false;
                if (_lightCapture != null && _lightCapture.enabled) _lightCapture.enabled = false;
                if (_lights != null && _lights.enabled) _lights.enabled = false;
            }

            // 时代滤镜贴在主相机前，不能拍进地图（地球仪相机前另有一份）
            void PreCull(Camera pCamera)
            {
                if (_capture == null || pCamera != _capture) return;
                foreach (var (from, _) in _eras)
                {
                    if (from == null || !from.enabled) continue;
                    from.enabled = false;
                    _erasHidden.Add(from);
                }
            }

            void PostRender(Camera pCamera)
            {
                if (pCamera == null) return;
                if (pCamera == _capture)
                {
                    foreach (SpriteRenderer era in _erasHidden)
                        if (era != null) era.enabled = true;
                    _erasHidden.Clear();
                }
                else if (pCamera == _camera) PutBackMarkers();
            }

            static Color Tint(Material pMaterial) =>
                pMaterial != null && pMaterial.HasProperty("_Color") ? pMaterial.color : Color.white;

            MeshRenderer NewRenderer(string pName, Mesh pMesh, Material pMaterial)
            {
                var go = new GameObject(pName) { layer = Layer };
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = pMesh;
                MeshRenderer renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = new Material(pMaterial);
                return renderer;
            }

            // 横向按经度、纵向按纬度把网格卷到半径 R 的球上，再绕横轴转，让相机正对的那一点朝向屏幕
            void Shape(float pCx, float pCy, double pRadius, Color pSky)
            {
                double w = _map.width, h = _map.height;
                _cx = pCx;
                _cy = pCy;
                _radius = pRadius;
                _midY = _map.y + h / 2.0;
                double tilt = (pCy - _midY) / pRadius;
                _cosT = Math.Cos(tilt);
                _sinT = Math.Sin(tilt);

                for (int i = 0; i <= Rows; i++)
                for (int j = 0; j <= Cols; j++)
                {
                    int n = i * (Cols + 1) + j;
                    _verts[n] = Project(_map.x + w * j / Cols, _map.y + h * i / Rows, out double facing);
                    _colors[n] = Lit(Color.white, facing);
                }

                // 球背面的格子不画（材质不写深度，背面会透过来）
                _tris.Clear();
                for (int i = 0; i < Rows; i++)
                for (int j = 0; j < Cols; j++)
                {
                    int a = i * (Cols + 1) + j, b = a + 1, c = a + Cols + 1, d = c + 1;
                    float z = _verts[a].z + _verts[b].z + _verts[c].z + _verts[d].z;
                    if (z * 0.25f > pRadius) continue;
                    _tris.Add(a); _tris.Add(c); _tris.Add(b);
                    _tris.Add(b); _tris.Add(c); _tris.Add(d);
                }
                _mesh.vertices = _verts;
                _mesh.colors32 = _colors;
                _mesh.SetTriangles(_tris, 0, false);
                var bounds = new Bounds(new Vector3(pCx, pCy, (float)pRadius), Vector3.one * (float)(pRadius * 4 + w + h));
                _mesh.bounds = bounds;

                // 大气：刚开始弯时是原版地图外面那片蓝，铺满屏幕；越往外拉，从屏幕四边开始变薄、透出太空，
                // 弯成球时收拢成球外一圈光。像从低空升到太空，头顶的天越来越暗，最后只剩地平线上一道蓝边
                double cover = _size * Math.Sqrt(1 + _camera.aspect * _camera.aspect);
                double solid = cover * (1 - Bend) + pRadius * 0.98 * Bend;
                double fade = cover * 1.3 * (1 - Bend) + pRadius * (1 + Halo) * Bend;
                Color glow = Atmosphere * Tint(_terrain.sprRnd.sharedMaterial);
                Color inner = Color.Lerp(pSky, glow, Bend);
                inner.a = Mathf.Lerp(1f, Atmosphere.a, Bend);
                _haloVerts[0] = new Vector3(pCx, pCy, (float)pRadius);
                _haloColors[0] = inner;
                for (int k = 0; k < Segments; k++)
                {
                    double a = 2 * Math.PI * k / Segments;
                    double ca = Math.Cos(a), sa = Math.Sin(a);
                    _haloVerts[1 + k] = new Vector3((float)(pCx + solid * ca), (float)(pCy + solid * sa), (float)pRadius);
                    _haloVerts[1 + Segments + k] = new Vector3((float)(pCx + fade * ca), (float)(pCy + fade * sa), (float)pRadius);
                    _haloColors[1 + k] = inner;
                    _haloColors[1 + Segments + k] = new Color(inner.r, inner.g, inner.b, 0f);
                }
                _haloMesh.vertices = _haloVerts;
                _haloMesh.colors32 = _haloColors;
                _haloMesh.bounds = bounds;
            }

            // 平面地图上的一点卷到球上，返回它在地球仪相机里的位置。z 越小越靠近相机：正面在 0 附近，背面最远到 2R。
            // pFacing 是朝向相机的程度：正面 1，边缘 0，背面为负
            Vector3 Project(double pX, double pY, out double pFacing)
            {
                double lat = (pY - _midY) / _radius;
                double lon = (pX - _cx) / _radius;
                double cosLat = Math.Cos(lat);
                double px = _radius * cosLat * Math.Sin(lon);
                double py = _radius * Math.Sin(lat);
                double pz = -_radius * cosLat * Math.Cos(lon);
                double ry = py * _cosT + pz * _sinT;
                double rz = -py * _sinT + pz * _cosT;
                pFacing = -rz / _radius;
                return new Vector3((float)(_cx + px), (float)(_cy + ry), (float)(rz + _radius));
            }

            // 反过来：地球仪相机里的一点落在球的正面哪里，换回平面地图上的位置。落在球外或地图外返回 false
            bool Unproject(double pX, double pY, out Vector2 pFlat)
            {
                pFlat = Globals.POINT_IN_VOID_2;
                double px = pX - _cx, ry = pY - _cy;
                double d = _radius * _radius - px * px - ry * ry;
                if (d < 0) return false;
                double rz = -Math.Sqrt(d);
                double py = ry * _cosT - rz * _sinT;
                double pz = ry * _sinT + rz * _cosT;
                double lat = Math.Asin(Math.Max(-1.0, Math.Min(1.0, py / _radius)));
                double lon = Math.Atan2(px, -pz);
                double x = _cx + lon * _radius, y = _midY + lat * _radius;
                // 网格从地图左边开始绕，经度换回横坐标时落到地图那一段里
                double turn = 2 * Math.PI * _radius;
                while (x < _map.xMin) x += turn;
                while (x >= _map.xMax) x -= turn;
                if (x < _map.xMin || y < _map.yMin || y >= _map.yMax) return false;
                pFlat = new Vector2((float)x, (float)y);
                return true;
            }

            // 鼠标下面是平面地图上的哪一点
            public bool Mouse(out Vector2 pFlat)
            {
                pFlat = Globals.POINT_IN_VOID_2;
                if (_camera == null || !_camera.enabled) return false;
                Vector3 at = _camera.ScreenToWorldPoint(Input.mousePosition);
                return Unproject(at.x, at.y, out pFlat);
            }

            // 平面地图上的一点在屏幕上的位置，转到背面时放到屏幕外
            public Vector2 ToScreen(Vector3 pFlat)
            {
                if (_camera == null || !_camera.enabled) return Globals.POINT_IN_VOID_2;
                Vector3 at = Project(pFlat.x, pFlat.y, out double facing);
                if (facing < 0) return Globals.POINT_IN_VOID_2;
                return _camera.WorldToScreenPoint(at);
            }

            // 随缩放变大的标记（军队、国王、战斗、船……）立到球面对应的位置上，转到背面就藏起来。
            // 这些标记放到地球仪这一层，拍摄相机就拍不到它们；箭头和连线是贴在地面上的，照样拍进地图
            void PlaceMarkers()
            {
                float margin = _map.height * MarkerMargin;
                foreach (QuantumSpriteAsset asset in AssetManager.quantum_sprites.list)
                {
                    QuantumSpriteGroupSystem system = asset.group_system;
                    if (!asset.add_camera_zoom_multiplier || system == null) continue;
                    QuantumSprite[] all = system.getAll();
                    int count = system.countActive();
                    for (int i = 0; i < count; i++)
                    {
                        QuantumSprite sprite = all[i];
                        if (sprite == null || sprite is QuantumSpriteArrows || !sprite.gameObject.activeInHierarchy) continue;
                        if (asset.turn_off_renderer && sprite.has_sprite_renderer && !sprite.sprite_renderer.enabled) continue;
                        Transform t = sprite.transform;
                        Vector3 flat = t.position;
                        _moved.Add((t, t.localPosition));
                        Relayer(sprite.gameObject);
                        bool far = flat.x < _map.xMin - margin || flat.x > _map.xMax + margin
                            || flat.y < _map.yMin - margin || flat.y > _map.yMax + margin;
                        Vector3 at = Project(flat.x, flat.y, out double facing);
                        t.position = far || facing < 0 ? Globals.POINT_IN_VOID : new Vector3(at.x, at.y, flat.z);
                    }
                }
            }

            void Relayer(GameObject pObject)
            {
                _relayered.Add((pObject, pObject.layer));
                pObject.layer = Layer;
                Transform t = pObject.transform;
                for (int i = 0; i < t.childCount; i++) Relayer(t.GetChild(i).gameObject);
            }

            void PutBackMarkers()
            {
                for (int i = _moved.Count - 1; i >= 0; i--)
                {
                    var (t, local) = _moved[i];
                    if (t != null) t.localPosition = local;
                }
                for (int i = _relayered.Count - 1; i >= 0; i--)
                {
                    var (go, layer) = _relayered[i];
                    if (go != null) go.layer = layer;
                }
                _moved.Clear();
                _relayered.Clear();
            }

            // 星空铺满地球仪相机的视野，每个星点在屏幕上大小固定；转球时跟着相机位置微微移动
            void UpdateStars(Vector3 pCenter)
            {
                if (_space != null)
                {
                    // 和原版加载界面一样铺满屏幕，多放大一点留出移动的余量
                    float viewH = _size * 2f, viewW = viewH * _camera.aspect;
                    Vector2 art = _space.sprite.bounds.size;
                    float scale = Mathf.Max(viewW / art.x, viewH / art.y) * (1f + SpaceDrift * 2f);
                    float dx = (pCenter.x - _map.center.x) / Mathf.Max(1f, _map.width);
                    float dy = (pCenter.y - _map.center.y) / Mathf.Max(1f, _map.height);
                    _space.transform.localScale = new Vector3(scale, scale, 1f);
                    _space.transform.localPosition = new Vector3(-dx * SpaceDrift * viewW, -dy * SpaceDrift * viewH, 5f);
                    _space.color = Color.white;     // 一开始被大气盖住，大气变薄时从屏幕四边露出来
                    return;
                }
                float h = _size * 2f, w = h * _camera.aspect;
                float pw = _camera.pixelWidth / (float)StarSize, ph = _camera.pixelHeight / (float)StarSize;
                float ox = pCenter.x / Mathf.Max(1f, _map.width) * StarDrift, oy = pCenter.y / Mathf.Max(1f, _map.height) * StarDrift;
                _starMesh.vertices = new[]
                {
                    new Vector3(-w / 2, -h / 2, 5f), new Vector3(w / 2, -h / 2, 5f),
                    new Vector3(-w / 2, h / 2, 5f), new Vector3(w / 2, h / 2, 5f)
                };
                _starMesh.uv = new[] { new Vector2(ox, oy), new Vector2(ox + pw, oy), new Vector2(ox, oy + ph), new Vector2(ox + pw, oy + ph) };
                var c = new Color32(255, 255, 255, (byte)(255 * Mathf.Clamp01(Bend * 2f - 0.5f)));
                _starMesh.colors32 = new[] { c, c, c, c };
                _starMesh.bounds = new Bounds(Vector3.zero, new Vector3(w * 2, h * 2, 20f));
            }

            // 时代滤镜：大小按两台相机的视野换算，颜色和开关照原版
            void UpdateEras(float pZoom)
            {
                Transform main = MoveCamera.instance.main_camera.transform;
                float k = _size / Mathf.Max(0.01f, pZoom);
                foreach (var (from, to) in _eras)
                {
                    if (from == null || to == null) continue;
                    bool on = from.enabled && from.gameObject.activeInHierarchy && from.sprite != null;
                    to.enabled = on;
                    if (!on) continue;
                    to.sprite = from.sprite;
                    to.color = from.color;
                    to.sortingLayerID = from.sortingLayerID;
                    to.sortingOrder = from.sortingOrder;
                    Vector3 offset = from.transform.position - main.position;
                    to.transform.localPosition = new Vector3(offset.x * k, offset.y * k, 1f);
                    to.transform.localRotation = from.transform.rotation;
                    to.transform.localScale = from.transform.lossyScale * k;
                }
            }

            // 原版加载界面的背景图（星空和星云）
            static Sprite SpaceSprite()
            {
                foreach (LoadingScreen screen in Resources.FindObjectsOfTypeAll<LoadingScreen>())
                    if (screen.background != null && screen.background.sprite != null) return screen.background.sprite;
                return null;
            }

            // 随机星点：大多是暗淡的白点，少数亮星带一点蓝或黄
            static Texture2D StarTexture()
            {
                var texture = new Texture2D(StarSize, StarSize, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Repeat
                };
                var pixels = new Color32[StarSize * StarSize];
                var random = new System.Random(20261022);
                for (int n = 0; n < StarSize * StarSize / 220; n++)
                {
                    int x = random.Next(StarSize), y = random.Next(StarSize);
                    double roll = random.NextDouble();
                    byte bright = (byte)(60 + 195 * Math.Pow(random.NextDouble(), 3));
                    Color32 c = roll < 0.08 ? new Color32((byte)(bright * 0.75), (byte)(bright * 0.85), bright, 255)
                        : roll < 0.14 ? new Color32(bright, (byte)(bright * 0.9), (byte)(bright * 0.7), 255)
                        : new Color32(bright, bright, bright, 255);
                    pixels[y * StarSize + x] = c;
                    // 少数亮星画成小十字
                    if (bright > 200 && x > 0 && y > 0 && x < StarSize - 1 && y < StarSize - 1)
                    {
                        var dim = new Color32((byte)(c.r / 3), (byte)(c.g / 3), (byte)(c.b / 3), 255);
                        pixels[y * StarSize + x - 1] = dim;
                        pixels[y * StarSize + x + 1] = dim;
                        pixels[(y - 1) * StarSize + x] = dim;
                        pixels[(y + 1) * StarSize + x] = dim;
                    }
                }
                texture.SetPixels32(pixels);
                texture.Apply();
                return texture;
            }

            // 朝向相机的程度 pFacing（正面 1，边缘 0）越小越暗；还没弯起来时不加明暗
            Color32 Lit(Color pColor, double pFacing)
            {
                float f = Mathf.Clamp01((float)pFacing);
                float light = 1f - Shade * Bend * (1f - Mathf.Sqrt(f));
                return new Color(pColor.r * light, pColor.g * light, pColor.b * light, pColor.a);
            }

            void Hide()
            {
                _dragging = false;
                PutBackMarkers();
                if (_mainMask >= 0)
                {
                    Camera main = MoveCamera.instance != null ? MoveCamera.instance.main_camera : null;
                    if (main != null && main.cullingMask == 0) main.cullingMask = _mainMask;
                    _mainMask = -1;
                }
                if (_capture != null && _capture.enabled) _capture.enabled = false;
                HideLights();
                if (_camera != null && _camera.enabled) _camera.enabled = false;
                if (_surface != null && _surface.enabled) _surface.enabled = false;
                if (_halo != null && _halo.enabled) _halo.enabled = false;
                if (_stars != null && _stars.enabled) _stars.enabled = false;
                if (_space != null && _space.enabled) _space.enabled = false;
                foreach (var (_, to) in _eras)
                    if (to != null && to.enabled) to.enabled = false;
            }

            // 拖动转球：鼠标在球的正面移动多少，球面就跟着转多少。横向首尾相接，纵向到两极为止。
            // 用哪些键拖动照原版：右键、中键；左键留给神力和点选。触屏用手指（几根手指取中点）拖
            public void Drag(MoveCamera pMove)
            {
                pMove._move_velocity = Vector2.zero;
                MoveCamera.camera_drag_run = false;
                Vector3 center = default;
                int touches = Input.mousePresent ? 0 : TouchCenter(out center);
                bool held = touches > 0 || Input.GetMouseButton(1) || Input.GetMouseButton(2);
                if (!held)
                {
                    _dragging = false;
                    _touches = 0;
                    if (MoveCamera.camera_drag_activated && Time.frameCount > MoveCamera.camera_drag_activated_frame + 2)
                        MoveCamera.camera_drag_activated = false;
                    return;
                }
                Vector3 point = touches > 0 ? center : Input.mousePosition;
                // 刚按下，或者手指数变了（第二根手指按下、抬起一根），从现在的位置算起，免得球跳一下
                if (!_dragging || touches != _touches)
                {
                    if (!_dragging && World.world.isOverUI()) return;
                    _dragging = true;
                    _touches = touches;
                    _lastMouse = point;
                    return;
                }
                if (touches > 0)
                {
                    // 触屏照原版：单指按下的头几帧可能是在放神力，先不转；过了这几帧一动就算拖动，这次按下不再放神力
                    PlayerControl control = World.world.player_control;
                    if (control.touch_ticks_skip > 5)
                    {
                        control.already_used_zoom = true;
                        control.already_used_power = false;
                    }
                    else if (touches == 1) return;
                }
                MoveCamera.camera_drag_run = true;
                Vector3 delta = point - _lastMouse;
                _lastMouse = point;
                if (delta.sqrMagnitude > 0f)
                {
                    MoveCamera.camera_drag_activated = true;
                    MoveCamera.camera_drag_activated_frame = Time.frameCount;
                }
                float unit = _size * 2f / Mathf.Max(1, _camera.pixelHeight);
                Vector3 pos = pMove.transform.position;
                float x = _map.x + Mathf.Repeat(pos.x - delta.x * unit - _map.x, _map.width);
                float y = Mathf.Clamp(pos.y - delta.y * unit, _map.yMin, _map.yMax);
                pMove.transform.position = new Vector3(x, y, pos.z);
            }

            // 按着的手指数和它们的中点
            static int TouchCenter(out Vector3 pCenter)
            {
                pCenter = Vector3.zero;
                int count = 0;
                for (int i = 0; i < Input.touchCount; i++)
                {
                    Touch touch = Input.GetTouch(i);
                    if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled) continue;
                    pCenter += (Vector3)touch.position;
                    count++;
                }
                if (count > 0) pCenter /= count;
                return count;
            }
        }

        // 地球仪显示时，拖动交给地球仪转球
        [HarmonyPatch(typeof(MoveCamera), "updateMouseCameraDrag")]
        static class Patch_Drag
        {
            static bool Prefix(MoveCamera __instance)
            {
                if (!Active || !Guard.On) return true;
                try { _view.Drag(__instance); return false; }
                catch (Exception e) { Guard.Fail("地球仪·拖动", e); return true; }
            }
        }

        // 鼠标的位置：原版按主相机把屏幕坐标换成地图坐标，地球仪显示时换成鼠标下面那块球面对应的地图位置
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.getMousePos))]
        static class Patch_MousePos
        {
            static bool Prefix(ref Vector2 __result)
            {
                if (!Active || !Guard.On) return true;
                try
                {
                    _view.Mouse(out __result);
                    return false;
                }
                catch (Exception e) { Guard.Fail("地球仪·鼠标", e); return true; }
            }
        }

        // 鼠标下面的地块：原版用主相机的射线找地形层上的像素，地球仪显示时按球面反算
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.getMouseTilePos))]
        static class Patch_MouseTile
        {
            static bool Prefix(ref WorldTile __result)
            {
                if (!Active || !Guard.On) return true;
                try
                {
                    __result = _view.Mouse(out Vector2 at) ? World.world.GetTile((int)at.x, (int)at.y) : null;
                    return false;
                }
                catch (Exception e) { Guard.Fail("地球仪·鼠标", e); return true; }
            }
        }

        // 名牌的屏幕位置：原版按主相机算，地球仪显示时按球面算，转到背面的放到屏幕外
        [HarmonyPatch(typeof(NameplateText), "transformPosition")]
        static class Patch_NameplatePosition
        {
            static bool Prefix(Vector3 pVec, ref Vector2 __result)
            {
                if (!Guard.On) return true;
                try
                {
                    if (!Active) return true;
                    __result = _view.ToScreen(pVec);
                    return false;
                }
                catch (Exception e) { Guard.Fail("地球仪·名牌", e); return true; }
            }
        }

        // 鼠标指着哪个名牌：原版把鼠标的地图位置用主相机换回屏幕坐标再比，地球仪显示时直接用鼠标的屏幕坐标，其余照原版
        [HarmonyPatch(typeof(NameplateManager), "findObjectForTooltip")]
        static class Patch_NameplateHover
        {
            static bool Prefix(NameplateManager __instance, ref NanoObject __result)
            {
                if (!Active || !Guard.On) return true;
                try
                {
                    __result = Hover(__instance);
                    return false;
                }
                catch (Exception e) { Guard.Fail("地球仪·名牌", e); return true; }
            }

            static NanoObject Hover(NameplateManager pManager)
            {
                pManager.cursor_over_text = null;
                if (World.world.isBusyWithUI()) return null;
                if (ControllableUnit.isControllingUnit()) return null;
                if (!InputHelpers.mouseSupported && !pManager.checkTouch(out _)) return null;
                Vector2 point = Input.mousePosition;
                NanoObject result = null;
                float best = float.MaxValue;
                NameplateText found = null;
                foreach (NameplateText text in pManager._active)
                {
                    if (!text.isShowing()) continue;
                    Vector2 at = text.getLastScreenPosition();
                    float dist = Toolbox.SquaredDist(at.x, at.y, point.x, point.y);
                    if (text.map_text_rect_click.Contains(point) && (found == null || (dist <= best && dist <= 625f)))
                    {
                        found = text;
                        best = dist;
                    }
                }
                if (found == null) return null;
                NanoObject nano = found.nano_object;
                if (Input.mousePresent) result = nano;
                pManager.cursor_over_text = found;
                found.forceScale(found.transform.localScale * 1.1f);
                if (nano is IMetaObject meta && InputHelpers.mouseSupported) meta.setCursorOver();
                return result;
            }
        }
    }
}
