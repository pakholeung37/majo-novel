import argparse,collections,csv,json,math
from pathlib import Path

def stats(rows):
    a=sorted(float(r['gap_ms']) for r in rows)
    if not a:return {}
    def percentile(values,q):return sorted(values)[min(len(values)-1,int(len(values)*q))]
    result={'frames':len(a),'fps':1000/(sum(a)/len(a)),'p50Ms':percentile(a,.5),'p95Ms':percentile(a,.95),'p99Ms':percentile(a,.99),'maxMs':max(a),'over16_67':sum(x>1000/60 for x in a),'over25':sum(x>25 for x in a),'over50':sum(x>50 for x in a),'focusedPercent':100*sum(r['focused']=='True' for r in rows)/len(rows)}
    for key in ['gpu_ms','render_cpu_ms']:
        values=[float(r[key]) for r in rows if key in r and math.isfinite(float(r[key])) and float(r[key])>0]
        if values:result[key]={'p50':percentile(values,.5),'p95':percentile(values,.95),'p99':percentile(values,.99),'max':max(values)}
    return result

def analyze(directory):
    p=Path(directory);frames=list(csv.DictReader((p/'frames.csv').open()));spans=json.loads((p/'spans.json').read_text());lines=json.loads((p/'lines.json').read_text());by_id={s['Id']:s for s in spans}
    groups={'startup':[r for r in frames if int(r['line'])==0],'opening':[r for r in frames if int(r['line'])==1],'progression':[r for r in frames if int(r['line'])>=2]}
    def command(s):
        seen=set()
        while s['Parent'] in by_id and s['Name']!='command' and s['Id'] not in seen:
            seen.add(s['Id']);s=by_id[s['Parent']]
        return s['Tags'] if s['Name']=='command' else None
    by_name=collections.defaultdict(list)
    for s in spans:by_name[s['Name']].append(s['DurationMs'])
    span_stats={k:{'count':len(v),'totalMs':sum(v),'p50Ms':sorted(v)[len(v)//2],'p95Ms':sorted(v)[int(len(v)*.95)],'maxMs':max(v)} for k,v in by_name.items()}
    long=[]
    for i,r in enumerate(frames):
        if int(r['line'])<2 or float(r['gap_ms'])<=1000/60:continue
        start=float(r['time_ms'])-float(r['gap_ms']);end=float(r['time_ms'])
        overlap=[s for s in spans if s['StartMs']<end and s['StartMs']+s['DurationMs']>start]
        long.append({'timeMs':end,'gapMs':float(r['gap_ms']),'line':int(r['line']),'phase':r['phase'],'gcChanged':any(int(r[k])>int(frames[i-1][k]) for k in ['gc0','gc1','gc2']), 'overlappingSpans':[{'name':s['Name'],'startMs':s['StartMs'],'durationMs':s['DurationMs'],'tags':s['Tags']} for s in overlap]})
    same=[{'startMs':s['StartMs'],'durationMs':s['DurationMs'],'command':command(s)} for s in spans if s['Name']=='layered.appearance.setup.sync' and s['Tags'].get('sameLayers')]
    result={'configuration':json.loads((p/'configuration.json').read_text()),'phases':{k:stats(v) for k,v in groups.items()},'lines':[{**line,'stats':stats([r for r in frames if int(r['line'])==line['number']])} for line in lines], 'spanStats':span_stats,'unchangedAppearancePreparations':same,'progressionLongFramesWithGC':sum(r['gcChanged'] for r in long),'longFrames':sorted(long,key=lambda r:-r['gapMs']),'gpuTimingAvailable':any(float(r.get('gpu_ms',0))>0 for r in frames), 'invalidTextureSamples':sum(not math.isfinite(float(r['texture_mib'])) or float(r['texture_mib'])>1048576 for r in frames), 'notes':['All-zero GPU timing is unavailable, not zero GPU cost.','Activity durations are elapsed latency; nested spans must not be summed as CPU time.','GPU queries report previous rendering submissions; mode changes can temporarily misalign samples.','Godot Performance monitors may lag; wrapped texture counters are invalid.']}
    (p/'summary.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n')
    print(json.dumps({k:v for k,v in result.items() if k not in ['lines','longFrames']},ensure_ascii=False,indent=2))

if __name__=='__main__':
    parser=argparse.ArgumentParser(description='Summarize a --trial00-progress trace without third-party dependencies.')
    parser.add_argument('directory');args=parser.parse_args();analyze(args.directory)
