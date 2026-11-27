"""Generate SDK proxy shaders + data files from the extracted game data."""
import json, os, re

SDK = '/home/claude/out/HandsOverSkins/SDK/Assets/HandsOverSkinSDK'
props = json.load(open('/home/claude/shader_props.json'))
rigs = json.load(open('/home/claude/rigs.json'))
os.makedirs(SDK + '/Shaders', exist_ok=True)
os.makedirs(SDK + '/Data', exist_ok=True)

MODES = {
    'Dissolve/SH_Dissolve_Lit5': 'DISSOLVE',
    'Dissolve/SH_Dissolve_SimpleLit': 'DISSOLVE',
    'Shader Graphs/SG_AStencilLIt5': 'LIT',
    'Shader Graphs/LitTranspWithFog': 'LIT_TRANSPARENT',
    'Shader Graphs/UnlitTranspWithFog5': 'UNLIT_TRANSPARENT',
    'MS/Ghost Unlit': 'GHOST',
}

def fnum(v):
    s = ('%.4f' % v).rstrip('0').rstrip('.')
    return s if s not in ('-0', '') else '0'

def prop_line(p):
    n = p['name']
    if n.startswith('unity_'): return None
    flags = p['flags']; attrs = []
    render_state = re.match(r'_(Src|Dst|Z|Surface|Blend|Cull|Queue|Alpha|Cast|Receive|Workflow|Stencil|XR|Add)', n)
    if flags & 0x1 and render_state: attrs.append('[HideInInspector]')
    if flags & 0x80: attrs.append('[MainTexture]')
    if flags & 0x100: attrs.append('[MainColor]')
    if flags & 0x8: attrs.append('[Normal]')
    if flags & 0x10: attrs.append('[HDR]')
    if flags & 0x4 and p['type'] == 'Texture': attrs.append('[NoScaleOffset]')
    for a in p['attributes']: attrs.append('[%s]' % a)
    d = p['default']; desc = p['description'].replace('"', "'")
    t = p['type']
    if t == 'Texture':
        tex = p['defaultTexture'] or 'white'
        if tex == 'grey': tex = 'gray'
        decl = '"%s", 2D) = "%s" {}' % (desc, tex)
    elif t == 'Color':
        decl = '"%s", Color) = (%s)' % (desc, ', '.join(fnum(x) for x in d))
    elif t == 'Vector':
        decl = '"%s", Vector) = (%s)' % (desc, ', '.join(fnum(x) for x in d))
    elif t == 'Range':
        decl = '"%s", Range(%s, %s)) = %s' % (desc, fnum(d[1]), fnum(d[2]), fnum(d[0]))
    elif t == 'Int':
        decl = '"%s", Int) = %s' % (desc, int(d[0]))
    else:
        decl = '"%s", Float) = %s' % (desc, fnum(d[0]))
    return '        %s%s (%s' % (' '.join(attrs) + (' ' if attrs else ''), n, decl)

HLSL = r'''
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);  SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);  SAMPLER(sampler_BumpMap);
            TEXTURE2D(_NoiseMap); SAMPLER(sampler_NoiseMap);
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _BumpScale;
            half _Opacity_Intensity;
            half _DissolveAmount, _NoiseScale, _NoiseSharpness, _EdgeWidth, _EdgeIntensity;
            half4 _EdgeColor;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 tangentOS : TANGENT; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 tangentWS : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
                float3 positionOS : TEXCOORD4;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                VertexNormalInputs n = GetVertexNormalInputs(v.normalOS, v.tangentOS);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.positionOS = v.positionOS.xyz;
                float4 st = _BaseMap_ST;
                if (st.x == 0 && st.y == 0) st = float4(1, 1, 0, 0); // [NoScaleOffset] textures leave _ST unset
                o.uv = v.uv * st.xy + st.zw;
                o.normalWS = n.normalWS;
                o.tangentWS = float4(n.tangentWS, v.tangentOS.w * GetOddNegativeScale());
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv) * _BaseColor;
                half3 nTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, i.uv), max(_BumpScale, 0.0001));
                float3 bitangent = i.tangentWS.w * cross(i.normalWS, i.tangentWS.xyz);
                float3 nWS = normalize(TransformTangentToWorld(nTS, half3x3(i.tangentWS.xyz, bitangent, i.normalWS)));
                float3 viewWS = GetWorldSpaceNormalizeViewDir(i.positionWS);

            #if defined(HO_GHOST)
                half fres = pow(1.0 - saturate(dot(nWS, viewWS)), 1.5);
                half3 col = lerp(albedo.rgb, half3(1, 1, 1), fres);
                return half4(col, saturate(albedo.a * _Opacity_Intensity + fres * 0.5));
            #elif defined(HO_UNLIT)
                return albedo;
            #else
                Light light = GetMainLight();
                half ndl = saturate(dot(nWS, light.direction));
                half3 col = albedo.rgb * (light.color * ndl + SampleSH(nWS));
              #if defined(HO_DISSOLVE)
                half noise = SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, i.uv * _NoiseScale).r;
                noise = saturate((noise - 0.5) * _NoiseSharpness + 0.5);
                half d = noise - _DissolveAmount;
                clip(d);
                col += _EdgeColor.rgb * _EdgeIntensity * (1.0 - smoothstep(0.0, _EdgeWidth, d)) * step(0.001, _DissolveAmount);
              #endif
                return half4(col, albedo.a);
            #endif
            }
'''

def shader(name, plist, mode):
    names = {p['name'] for p in plist}
    lines = [l for l in (prop_line(p) for p in plist) if l]
    transparent = mode in ('GHOST', 'LIT_TRANSPARENT', 'UNLIT_TRANSPARENT')
    defines = {'DISSOLVE': '#define HO_DISSOLVE', 'GHOST': '#define HO_GHOST', 'UNLIT_TRANSPARENT': '#define HO_UNLIT'}.get(mode, '')
    if mode == 'GHOST':
        blend = 'Blend SrcAlpha OneMinusSrcAlpha\n            ZWrite Off'
    elif transparent:
        blend = 'Blend SrcAlpha OneMinusSrcAlpha\n            ZWrite Off'
    else:
        blend = 'ZWrite On'
    cull = 'Cull [_Cull]' if '_Cull' in names else 'Cull Back'
    tags = '"RenderPipeline"="UniversalPipeline" "RenderType"="%s" "Queue"="%s"' % (
        'Transparent' if transparent else 'Opaque', 'Transparent' if transparent else 'AlphaTest' if mode == 'DISSOLVE' else 'Geometry')
    return f'''// =====================================================================================
// HANDS OVER proxy shader: "{name}"
// Editor preview only. The property block is identical to the game's shader
// (extracted from the game build), so materials keep every value you set.
// At runtime HandsOverSkins replaces this shader with the game's real "{name}".
// Generated by the HANDS OVER Skin SDK, do not edit by hand.
// =====================================================================================
Shader "{name}"
{{
    Properties
    {{
{chr(10).join(lines)}
    }}

    SubShader
    {{
        Tags {{ {tags} }}
        LOD 300

        Pass
        {{
            Name "ForwardLit"
            Tags {{ "LightMode"="UniversalForward" }}
            {blend}
            {cull}

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            {defines}
{HLSL}
            ENDHLSL
        }}
    }}
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}}
'''

for name, plist in props.items():
    fn = re.sub(r'[^A-Za-z0-9]+', '_', name).strip('_') + '.shader'
    open(f'{SDK}/Shaders/{fn}', 'w').write(shader(name, plist, MODES[name]))
    print('wrote', fn)

# shaders.json: property names/types for validator + inspector hints
known = {n: [{'name': p['name'], 'type': p['type'], 'description': p['description']} for p in pl if not p['name'].startswith('unity_')]
         for n, pl in props.items()}
known['Universal Render Pipeline/Lit'] = 'builtin'
json.dump(known, open(f'{SDK}/Data/game_shaders.json', 'w'), indent=1)

# rigs.json: per character -> list of transform paths (relative to the SK_ prefab root)
rig_out = {}
for ch, paths in rigs.items():
    root = paths[0]
    rel = [p[len(root) + 1:] for p in paths[1:]]
    meshes = [p for p in rel if '/' not in p and p not in ('root',)]
    bones = [p for p in rel if p == 'root' or p.startswith('root/')]
    rig_out[ch] = {'prefab': root, 'defaultMeshes': [m for m in meshes if m != 'root'], 'bones': bones}
json.dump(rig_out, open(f'{SDK}/Data/character_rigs.json', 'w'), indent=1)
print({k: len(v['bones']) for k, v in rig_out.items()})
