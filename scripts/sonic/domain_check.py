"""Opt-in, read-only TEST audio check: full-file segments vs previews, then 128 kbps codec change.
Only 12 matched local files; no full-song copies, network or profile writes. ffmpeg pipes stay in memory.
"""
import argparse
import json
from pathlib import Path
import subprocess
import numpy as np
import onnxruntime as ort
from benchmark import pcm, features, normalize
from prepare import key

p=argparse.ArgumentParser()
p.add_argument('--work',type=Path,required=True)
p.add_argument('--test-profile',type=Path,required=True)
p.add_argument('--ffmpeg',required=True)
a=p.parse_args()
if a.test_profile.name!='profile' or a.test_profile.parent.name!='NoctisPlus-Test':
    raise ValueError('TEST profile only')
m=json.loads((a.work/'manifest.json').read_text())
vectors={k:np.array(v,dtype=np.float32) for k,v in json.loads((a.work/'clap-music-int8-vectors.json').read_text()).items()}
tracks={t['key']:t for t in m['tracks']}
library=json.loads((a.test_profile/'library.json').read_text(encoding='utf-8-sig'))
opts=ort.SessionOptions(); opts.intra_op_num_threads=4
session=ort.InferenceSession(str(a.work/'models/clap-music/audio-model-int8.onnx'),opts,providers=['CPUExecutionProvider'])
def embed(waves):
    return normalize(np.mean([normalize(session.run(None,{'input_features':features(w)})[0][0]) for w in waves],axis=0))
rows=[]
for song in library:
    k=key(song.get('artist',''),song.get('title',''))
    path=Path(song.get('filePath',''))
    if k not in vectors or not path.is_file() or any(s in str(path).lower() for s in ['audit','scratch','testlib']): continue
    try:
        parts=song['duration'].split(':'); duration=float(parts[-1])+60*int(parts[-2])+3600*int(parts[-3])
        waves=[pcm(a.ffmpeg,str(path),48000,max(0,min(duration-10,duration*f-5)),10) for f in [.2,.5,.8]]
        local=embed(waves)
        raw=np.concatenate(waves).astype('<f4').tobytes()
        encoded=subprocess.run([a.ffmpeg,'-v','error','-f','f32le','-ar','48000','-ac','1','-i','pipe:0','-c:a','libmp3lame','-b:a','128k','-f','mp3','pipe:1'],input=raw,capture_output=True,check=True,timeout=20).stdout
        decoded=subprocess.run([a.ffmpeg,'-v','error','-i','pipe:0','-ar','48000','-ac','1','-f','f32le','pipe:1'],input=encoded,capture_output=True,check=True,timeout=20).stdout
        wave=np.frombuffer(decoded,dtype='<f4')
        mp3=embed([wave[i*480000:(i+1)*480000] for i in range(3)])
        same_artist=[float(local@v) for t,v in vectors.items() if t!=k and t in tracks and key(tracks[t]['artist'],'').split(':')[0]==key(song['artist'],'').split(':')[0]]
        others=[float(local@v) for t,v in vectors.items() if t in tracks and key(tracks[t]['artist'],'').split(':')[0]!=key(song['artist'],'').split(':')[0]]
        rows.append(dict(same_song=float(local@vectors[k]),codec_cosine=float(local@mp3),same_artist_median=float(np.median(same_artist)) if same_artist else None,
                         other_artist_median=float(np.median(others)) if others else None,own_preview_rank=1+sum(float(local@v)>float(local@vectors[k]) for t,v in vectors.items() if t!=k)))
        print(f'domain pair {len(rows)}: same {rows[-1]["same_song"]:.3f}, codec {rows[-1]["codec_cosine"]:.3f}',flush=True)
    except Exception as ex:
        print('pair skipped: '+type(ex).__name__,flush=True)
    if len(rows)>=12: break
result=dict(pairs=len(rows),rows=rows,same_song_median=float(np.median([r['same_song'] for r in rows])) if rows else None,
            codec_median=float(np.median([r['codec_cosine'] for r in rows])) if rows else None,
            note='Small TEST-file domain diagnostic; does not establish preference quality. Local and preview vectors remain separate in production.')
(a.work/'domain-report.json').write_text(json.dumps(result,indent=2))
print(json.dumps(result,indent=2))
