#ifndef UNITY_LIGHTING_INCLUDED 
#define UNITY_LIGHTING_INCLUDED

#include "../Material/Material.hlsl"

#if UNITY_SHADERRENDERPASS == UNITY_SHADERRENDERPASS_FORWARD
#include "LightingForward.hlsl"
#endif

#endif // UNITY_LIGHTING_INCLUDED