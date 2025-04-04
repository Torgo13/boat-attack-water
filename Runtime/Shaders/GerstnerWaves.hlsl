#ifndef GERSTNER_WAVES_INCLUDED
#define GERSTNER_WAVES_INCLUDED

uniform uint _WaveCount; // how many waves, set via the water component

struct Wave
{
	float amplitude;
	float direction;
	float wavelength;
};

#if defined(USE_STRUCTURED_BUFFER)
StructuredBuffer<Wave> _WaveDataBuffer;
#else
half4 waveData[8]; // 0-7 amplitude, direction, wavelength
#endif

struct WaveStruct
{
	float3 position;
	float3 normal;
	float foam;
};

WaveStruct GerstnerWave(float2 pos, float waveCountMulti, half amplitude, half2 direction, half wavelength)
{
	WaveStruct waveOut;
#if defined(_STATIC_SHADER)
	float time = 0;
#else
	float time = _Time.y;
#endif

	////////////////////////////////wave value calculations//////////////////////////
	half3 wave = 0; // wave vector
	half wSpeed = sqrt(9.806 * wavelength); // frequency of the wave based off wavelength
	half peak = 2; // peak value, 1 is the sharpest peaks
	half wa = wavelength * amplitude;
	half qi = peak / (wa * _WaveCount);
	
	half2 windDir = direction; // calculate wind direction
	half dir = dot(windDir, pos); // calculate a gradient along the wind direction

	////////////////////////////position output calculations/////////////////////////
	half calc = dir * wavelength + -time * wSpeed; // the wave calculation
	//half cosCalc = cos(calc); // cosine version(used for horizontal undulation)
	//half sinCalc = sin(calc); // sin version(used for vertical undulation)
	half sinCalc, cosCalc;
	sincos(calc, sinCalc, cosCalc);

	// foam height raw
	half a = (sinCalc + 1) * 0.5;
	
	// calculate the offsets for the current point
	wave.xz = amplitude * cosCalc * qi * windDir;
	wave.y = sinCalc * amplitude * waveCountMulti;// the height is divided by the number of waves

	////////////////////////////normal output calculations/////////////////////////
	// normal vector
	half3 n = half3(-(cosCalc * wa * windDir),
					1-qi * wa * sinCalc);

	////////////////////////////////assign to output///////////////////////////////
	waveOut.position = wave * saturate(amplitude * 10000);
	waveOut.normal = (n.xzy * waveCountMulti);
	half b = dot(n.xy * 2, -windDir);
	waveOut.foam = saturate(a + b);

	return waveOut;
}

WaveStruct GerstnerWave(float2 pos, float waveCountMulti, half amplitude, half direction, half wavelength)
{
	direction = radians(direction); // convert the incoming degrees to radians, for directional waves
	half sd, cd;
	sincos(direction, sd, cd);
	half2 dirWaveInput = half2(cd, sd);

	//half2 windDir = normalize(dirWaveInput); // calculate wind direction
	return GerstnerWave(pos, waveCountMulti, amplitude, dirWaveInput, wavelength);
}

inline void SampleWaves(float3 position, half opacity, out WaveStruct waveOut)
{
	waveOut = (WaveStruct)0;
	half waveCountMulti = 1.0 / _WaveCount;
	opacity = saturate(opacity);

	UNITY_LOOP
	for (uint i = 0; i < _WaveCount; i++)
	{
#if defined(USE_STRUCTURED_BUFFER)
		Wave w = _WaveDataBuffer[i];
#else
		Wave w;
		w.amplitude = waveData[i].x;
		w.direction = waveData[i].y;
		w.wavelength = waveData[i].z;
#endif
		WaveStruct wave = GerstnerWave(position.xz,
								waveCountMulti,
								w.amplitude,
								w.direction,
								w.wavelength); // calculate the wave

		waveOut.position += wave.position; // add the position
		waveOut.normal += wave.normal; // add the normal
		waveOut.foam += wave.foam;
	}

	waveOut.position *= opacity;// opacityMask;
	waveOut.normal *= float3(opacity, 1, opacity);
	waveOut.foam *= waveCountMulti * opacity;
}

void Gerstner_SG_test_half(float2 pos, half amp, half2 dir, half length, out float3 position, out float3 normal)
{
	WaveStruct wave = GerstnerWave(pos,
		1,
		amp,
		dir,
		length
	);

	position = wave.position;
	normal = wave.normal;
}

#endif // GERSTNER_WAVES_INCLUDED