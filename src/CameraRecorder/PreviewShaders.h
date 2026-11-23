// Full-screen texture shaders from the supplied SpoutPreview.cpp.
const char* kVertexShader = R"(
struct VSOut
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

VSOut main(uint id : SV_VertexID)
{
    float2 p = float2((id == 1) ? 3.0 : -1.0, (id == 2) ? 3.0 : -1.0);
    VSOut o;
    o.pos = float4(p, 0.0, 1.0);
    // 归一化到 0..1，并翻转 Y（Spout 纹理原点在左上）
    o.uv  = float2((p.x + 1.0) * 0.5, 1.0 - (p.y + 1.0) * 0.5);
    return o;
}
)";

const char* kPixelShader = R"(
Texture2D    gTex : register(t0);
SamplerState gSmp : register(s0);

float4 main(float4 pos : SV_POSITION, float2 uv : TEXCOORD0) : SV_Target
{
    return gTex.Sample(gSmp, uv);
}
)";
