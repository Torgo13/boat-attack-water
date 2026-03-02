#ifndef GERSTNER_WAVES_INCLUDED
#define GERSTNER_WAVES_INCLUDED

uniform uint 	_WaveCount; // how many waves, set via the water component

struct Wave
{
	float amplitude;
	float direction;
	float wavelength;
	float omni;
	float2 origin;
};

#if defined(USE_STRUCTURED_BUFFER)
StructuredBuffer<Wave> _WaveDataBuffer;
#else
half4 waveData[20]; // 0-9 amplitude, direction, wavelength, omni, 10-19 origin.xy
#endif

struct WaveStruct
{
	float3 position;
	float3 normal;
};

WaveStruct GerstnerWave(half2 pos, float waveCountMulti, half amplitude, half direction, half wavelength, half omni, half2 omniPos)
{
	WaveStruct waveOut;
#if defined(_STATIC_SHADER)
	float time = 0;
#else
	float time = _Time.y;
#endif

	////////////////////////////////wave value calculations//////////////////////////
	half3 wave = 0; // wave vector
	half w = 6.28318 / wavelength; // 2pi over wavelength(hardcoded)
	half wSpeed = sqrt(9.8 * w); // frequency of the wave based off wavelength
	//half peak = 0.8; // peak value, 1 is the sharpest peaks
	half wa = w * amplitude;
	half qi = wavelength * (0.8 / (_WaveCount * 6.28318));

	half2 omniVec = -omniPos * omni;
	direction = radians(direction); // convert the incoming degrees to radians, for directional waves
	half2 dirWaveInput = half2(sin(direction), cos(direction)) * (1 - omni);
	half2 omniWaveInput = mad(pos, omni, omniVec);

	half2 windDir = normalize(dirWaveInput + omniWaveInput); // calculate wind direction
	half dir = dot(windDir, pos + omniVec); // calculate a gradient along the wind direction

	////////////////////////////position output calculations/////////////////////////
	half calc = dir * w + -time * wSpeed; // the wave calculation
	half cosCalc = cos(calc); // cosine version(used for horizontal undulation)
	half sinCalc = sin(calc); // sin version(used for vertical undulation)

	// calculate the offsets for the current point
	wave.xz = qi * cosCalc * windDir.xy;
	wave.y = ((sinCalc * amplitude)) * waveCountMulti;// the height is divided by the number of waves

	////////////////////////////normal output calculations/////////////////////////
	// normal vector
	half3 n = half3(-(windDir.xy * (wa * cosCalc)),
					1-(qi * (w * sinCalc)));

	////////////////////////////////assign to output///////////////////////////////
	waveOut.position = wave * saturate(amplitude * 10000);
	waveOut.normal = (n.xzy * waveCountMulti);

	return waveOut;
}

inline void SampleWaves(float3 position, half opacity, out WaveStruct waveOut)
{
	half2 pos = position.xz;
	waveOut.position = 0;
	waveOut.normal = 0;
	half waveCountMulti = 1.0 / _WaveCount;
	half3 opacityMask = saturate(half3(3, 3, 1) * opacity);

	UNITY_LOOP
	for(uint i = 0; i < _WaveCount; i++)
	{
#if defined(USE_STRUCTURED_BUFFER)
		Wave w = _WaveDataBuffer[i];
#else
		Wave w;
		w.amplitude = waveData[i].x;
		w.direction = waveData[i].y;
		w.wavelength = waveData[i].z;
		w.omni = waveData[i].w;
		w.origin = waveData[i + 10].xy;
#endif
		WaveStruct wave = GerstnerWave(pos,
								waveCountMulti,
								w.amplitude,
								w.direction,
								w.wavelength,
								w.omni,
								w.origin); // calculate the wave

		waveOut.position += wave.position; // add the position
		waveOut.normal += wave.normal; // add the normal
	}
	waveOut.position *= opacityMask;
	waveOut.normal *= half3(opacity, 1, opacity);
}

#endif // GERSTNER_WAVES_INCLUDED