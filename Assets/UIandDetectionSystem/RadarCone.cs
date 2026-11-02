using UnityEngine;
using UnityEngine.UI;

/// A filled wedge for the radar's vision cones. Points up (+Y), fades toward the edge.
public class RadarCone : MaskableGraphic
{
    float half = 45f, radius = 100f;

    public void Set(float halfAngle, float r)
    {
        if (Mathf.Abs(halfAngle - half) < .1f && Mathf.Abs(r - radius) < .5f) return;
        half = halfAngle; radius = r; SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Color edge = color; edge.a *= .15f;
        vh.AddVert(Vector3.zero, color, Vector2.zero);
        for (int i = 0; i <= 20; i++)
        {
            float a = Mathf.Deg2Rad * Mathf.Lerp(-half, half, i / 20f);
            vh.AddVert(new Vector3(Mathf.Sin(a), Mathf.Cos(a)) * radius, edge, Vector2.zero);
            if (i > 0) vh.AddTriangle(0, i, i + 1);
        }
    }
}