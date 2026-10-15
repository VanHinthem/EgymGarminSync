import { useEffect, useMemo, useState } from 'react'
import { ArrowRight, CircleAlert, CloudDownload, Dumbbell, Link2, RefreshCw, Save, Undo2 } from 'lucide-react'
import './style.css'

type Session = { id: string; completedAt: string | null; exerciseCount: number; method?: string | null; name?: string }
type SetRow = { garminSetIndex?: number; id: string; exercise: string; egymExercise?: string; garminExercise?: string; set: number; egymKg: number; garminKg: number; egymReps: number; garminReps: number }
type Draft = Record<string, { kg?: number; reps?: number }>
type SessionStatus = 'checking' | 'different' | 'matching' | 'unmatched' | 'unknown'
const exampleRows: SetRow[] = [
  {id:'cp1',exercise:'Chest Press',set:1,egymKg:21,garminKg:22,egymReps:15,garminReps:12},
  {id:'cp2',exercise:'Chest Press',set:2,egymKg:21,garminKg:22,egymReps:15,garminReps:15},
  {id:'sr1',exercise:'Seated Row',set:1,egymKg:32,garminKg:31,egymReps:15,garminReps:16},
  {id:'sr2',exercise:'Seated Row',set:2,egymKg:32,garminKg:31,egymReps:15,garminReps:15},
  {id:'lp1',exercise:'Leg Press',set:1,egymKg:95,garminKg:89,egymReps:15,garminReps:15},
  {id:'lp2',exercise:'Leg Press',set:2,egymKg:95,garminKg:89,egymReps:15,garminReps:15},
  {id:'lat1',exercise:'Lat Pulldown',set:1,egymKg:33,garminKg:33,egymReps:15,garminReps:15}
]
const demoEgym: Session[] = [
  {id:'demo-aug31',completedAt:'2026-08-31T08:32:00',exerciseCount:8},
  {id:'demo-aug19',completedAt:'2026-08-19T08:39:00',exerciseCount:8},
  {id:'demo-aug17',completedAt:'2026-08-17T08:45:00',exerciseCount:8}
]
const demoGarmin: Session[] = [
  {id:'24180037460',completedAt:'2026-08-31T08:30:00',exerciseCount:8},
  {id:'24032098016',completedAt:'2026-08-19T08:38:00',exerciseCount:8},
  {id:'24006250902',completedAt:'2026-08-17T08:44:00',exerciseCount:8}
]
function displayDate(value: string | null) {
  if (!value) return 'Datum onbekend'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleDateString('nl-NL',{day:'numeric',month:'long',year:'numeric'})
}
function App() {
  const [mode,setMode] = useState<'demo'|'live'>('demo')
  const [egym,setEgym] = useState<Session[]>(demoEgym)
  const [selectedEgym,setSelectedEgym] = useState('demo-aug31')
  const [selectedGarmin,setSelectedGarmin] = useState('24180037460')
  const [garmin,setGarmin] = useState<Session[]>(demoGarmin)
  const [draft,setDraft] = useState<Draft>({})
  const [differencesOnly,setDifferencesOnly] = useState(false)
  const [sessionStatuses,setSessionStatuses] = useState<Record<string,SessionStatus>>({})
  const [error,setError] = useState('')
  const [loading,setLoading] = useState(false)
  const [lastSync,setLastSync] = useState<string | null>(null)
  const [review,setReview] = useState(false)
  const [previewResult,setPreviewResult] = useState('')
  const [preparedBackupId,setPreparedBackupId] = useState<number|null>(null)
  const [writePlan,setWritePlan] = useState<{journalId:number;candidateSha256:string;outgoingSha256:string;canWrite:boolean}|null>(null)
  const [preparing,setPreparing] = useState(false)
  const [writing,setWriting] = useState(false)
  const [writeAttempted,setWriteAttempted] = useState(false)
  const [liveRows,setLiveRows] = useState<SetRow[]>([])
  const [cacheRevision,setCacheRevision] = useState(0)
  const [warnings,setWarnings] = useState<string[]>([])
  const [setsLoading,setSetsLoading] = useState(false)
  const [setsError,setSetsError] = useState('')
  const current = egym.find(s=>s.id===selectedEgym)
  function propose(source: Session | undefined, targets: Session[]) {
    if (!source?.completedAt || !source.method) return ''
    const completed = new Date(source.completedAt).getTime()
    const variant = source.method==='NEGATIVE_REPBASED' ? 'EGYM Negatief' :
      source.method==='REGULAR_REPBASED' ? 'EGYM Positief' : null
    if (!variant || Number.isNaN(completed)) return ''
    const nearby = targets.filter(t=>{
      const time = t.completedAt ? new Date(t.completedAt).getTime() : NaN
      return !Number.isNaN(time) && completed-time>=0 && completed-time<=120*60*1000
    })
    return nearby.length===1 && nearby[0].name===variant ? nearby[0].id : ''
  }
  const candidate = mode==='demo' ? demoGarmin.find(g=>g.completedAt?.slice(0,10)===current?.completedAt?.slice(0,10)) : garmin.find(g=>g.id===propose(current,garmin))
  useEffect(()=>{
    if(mode!=='live'||egym.length===0){setSessionStatuses({});return}
    let cancelled=false
    const recent=egym.slice(0,20)
    setSessionStatuses(Object.fromEntries(recent.map(s=>[s.id,propose(s,garmin)?'checking':'unmatched'])))
    // Cached comparisons only: no Garmin network requests, no writes.
    async function scan(){
      for(let index=0;index<recent.length;index+=4){
        if(cancelled)return
        const group=recent.slice(index,index+4).filter(s=>propose(s,garmin))
        const results=await Promise.all(group.map(async session=>{
          const match=propose(session,garmin)
          try{
            const response=await fetch(`/api/cache/compare?egymId=${encodeURIComponent(session.id)}&garminId=${encodeURIComponent(match)}`)
            if(!response.ok)return [session.id,'unknown'] as const
            const data=await response.json() as {rows:SetRow[];warnings:string[]}
            if(data.warnings?.length||!data.rows?.length)return [session.id,'unknown'] as const
            return [session.id,data.rows.some(r=>r.egymKg!==r.garminKg||r.egymReps!==r.garminReps)?'different':'matching'] as const
          }catch{return [session.id,'unknown'] as const}
        }))
        if(!cancelled)setSessionStatuses(prev=>({...prev,...Object.fromEntries(results)}))
      }
    }
    void scan()
    return ()=>{cancelled=true}
  },[mode,egym,garmin,cacheRevision])
  function statusLabel(id:string){
    switch(sessionStatuses[id]){
      case 'different':return '· Afwijkingen'
      case 'matching':return '· Komt overeen'
      case 'unmatched':return '· Geen unieke match'
      case 'unknown':return '· Controle nodig'
      case 'checking':return '· Controleren...'
      default:return ''
    }
  }
  const statusCounts = {
    different:Object.values(sessionStatuses).filter(s=>s==='different').length,
    matching:Object.values(sessionStatuses).filter(s=>s==='matching').length,
    pending:Object.values(sessionStatuses).filter(s=>s==='checking'||s==='unknown'||s==='unmatched').length
  }
  function nextDifferent(){
    const matches=egym.filter(s=>sessionStatuses[s.id]==='different')
    if(matches.length===0)return
    const currentIndex=matches.findIndex(s=>s.id===selectedEgym)
    const next=matches[(currentIndex+1)%matches.length]
    if(next)pickEgym(next.id)
  }
  const displayedRows = mode==='demo' ? exampleRows : liveRows
  const differs = (row:SetRow) => row.egymKg!==row.garminKg || row.egymReps!==row.garminReps
  const differenceCount = displayedRows.filter(differs).length
  const visibleRows = differencesOnly ? displayedRows.filter(differs) : displayedRows
  useEffect(()=>{
    if(mode!=='live'||!selectedEgym||!selectedGarmin){setLiveRows([]);setWarnings([]);setSetsError('');return}
    const abort=new AbortController()
    setSetsLoading(true);setLiveRows([]);setWarnings([]);setSetsError('')
    fetch(`/api/cache/compare?egymId=${encodeURIComponent(selectedEgym)}&garminId=${encodeURIComponent(selectedGarmin)}`,{signal:abort.signal})
      .then(async response=>{if(!response.ok)throw new Error(`Setvergelijking niet beschikbaar (HTTP ${response.status}).`);return response.json()})
      .then((data:{rows:SetRow[],warnings:string[]})=>{if(!abort.signal.aborted){setLiveRows(data.rows);setWarnings(data.warnings??[])}})
      .catch(err=>{if(!abort.signal.aborted)setSetsError(err instanceof Error?err.message:'Onbekende fout')})
      .finally(()=>{if(!abort.signal.aborted)setSetsLoading(false)})
    return ()=>abort.abort()
  },[mode,selectedEgym,selectedGarmin,cacheRevision])
  const changeList = useMemo(()=>displayedRows.flatMap(row=>{
    const state = draft[row.id] ?? {}
    const result: string[]=[]
    if (state.kg!==undefined && state.kg!==row.garminKg) result.push(`${row.exercise} set ${row.set}: ${row.garminKg} → ${state.kg} kg`)
    if (state.reps!==undefined && state.reps!==row.garminReps) result.push(`${row.exercise} set ${row.set}: ${row.garminReps} → ${state.reps} reps`)
    return result
  }),[draft,displayedRows])
  function stage(id:string,key:'kg'|'reps',value:number){setDraft(p=>({...p,[id]:{...p[id],[key]:value}}));setReview(false);setPreparedBackupId(null);setWritePlan(null);setWriteAttempted(false)}
  function selectedEdits(){
    return displayedRows.flatMap(r=>{
      const change=draft[r.id]??{}
      const values=[] as {setIndex:number,field:string,expected:number,value:number}[]
      if(r.garminSetIndex===undefined)return values
      if(change.kg!==undefined&&change.kg!==r.garminKg)values.push({setIndex:r.garminSetIndex,field:'weight',expected:r.garminKg*1000,value:change.kg*1000})
      if(change.reps!==undefined&&change.reps!==r.garminReps)values.push({setIndex:r.garminSetIndex,field:'repetitionCount',expected:r.garminReps,value:change.reps})
      return values
    })
  }
  async function checkServerPreview(prepare = false){
    setReview(true)
    if(mode==='demo')return
    const edits=selectedEdits()
    try{
      const response=await fetch(prepare?'/api/sync/prepare':'/api/sync/preview',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({garminId:Number(selectedGarmin),edits})})
      if(!response.ok){setPreviewResult('Servercontrole geweigerd: laad de sessies opnieuw voordat je verdergaat.');return}
      const result=await response.json() as {snapshotId?:number|null}
      if(prepare)setPreparedBackupId(result.snapshotId??null)
      setPreviewResult(prepare
        ? `Preflight-backup #${result.snapshotId} opgeslagen in SQLite. Er is niets naar Garmin geschreven.`
        : 'Servercontrole geslaagd: actuele Garmin-waarden komen overeen. Er is niets opgeslagen.')
    }catch{setPreviewResult('Servercontrole niet bereikbaar. Er is niets opgeslagen.')}
  }
  async function prepareSave(){
    if(mode!=='live'||changeList.length===0||preparing)return
    setPreparing(true);setWritePlan(null);setPreparedBackupId(null);setWriteAttempted(false)
    const activityId=Number(selectedGarmin)
    const edits=selectedEdits()
    try{
      const backup=await fetch('/api/sync/prepare',{method:'POST',headers:{'Content-Type':'application/json'},
        body:JSON.stringify({garminId:activityId,edits})})
      if(!backup.ok)throw new Error('Veiligheidscontrole mislukt. Controleer de Garmin-training opnieuw.')
      const snapshot=await backup.json() as {snapshotId?:number|null}
      if(snapshot.snapshotId==null)throw new Error('Er is geen veiligheidskopie ontvangen.')
      setPreparedBackupId(snapshot.snapshotId)
      const planResponse=await fetch('/api/sync/save-prepare',{method:'POST',
        headers:{'Content-Type':'application/json'},
        body:JSON.stringify({garminId:activityId,snapshotId:snapshot.snapshotId,edits})})
      if(!planResponse.ok)throw new Error('Opslagvoorbereiding geweigerd. Garmin kan gewijzigd zijn; sla niets op.')
      const result=await planResponse.json() as {journalId:number;candidateSha256:string;normalizedSha256:string;canWrite:boolean}
      setWritePlan({journalId:result.journalId,candidateSha256:result.candidateSha256,
        outgoingSha256:result.normalizedSha256,canWrite:result.canWrite})
      setPreviewResult('Veiligheidskopie en controles gereed. Wil je de getoonde wijzigingen definitief in Garmin opslaan?')
    }catch(e){
      setWritePlan(null)
      setPreviewResult(e instanceof Error?e.message:'Voorbereiding mislukt. Er is niets naar Garmin geschreven.')
    }finally{setPreparing(false)}
  }
  async function resumeWritePlan(){
    if(mode!=='live'||!selectedGarmin)return
    try{
      const [journalResponse,healthResponse]=await Promise.all([
        fetch(`/api/sync/write-journal/${selectedGarmin}`),
        fetch('/api/health')
      ])
      if(!journalResponse.ok||!healthResponse.ok)throw new Error('API niet bereikbaar')
      const journal=await journalResponse.json() as {records:{id:number;status:string;candidateSha256:string;proposedPayloadSha256:string}[]}
      const health=await healthResponse.json() as {writeEnabled:boolean}
      const ready=journal.records.find(r=>r.status==='Ready')
      if(!ready){setPreviewResult('Geen Ready schrijfvoorbereiding gevonden.');return}
      setWritePlan({journalId:ready.id,candidateSha256:ready.candidateSha256,
        outgoingSha256:ready.proposedPayloadSha256,canWrite:health.writeEnabled})
      setReview(true);setWriteAttempted(false)
      setPreviewResult(`Schrijfvoorbereiding #${ready.id} geladen. Controleer de exacte wijziging en bevestiging.`)
    }catch{setPreviewResult('Kon de voorbereiding niet laden.')}
  }
  async function cancelWrite(){
    if(mode!=='live'||!writePlan||writing||preparing)return
    const activityId=Number(selectedGarmin)
    const journalId=writePlan.journalId
    try{
      const response=await fetch('/api/sync/write-cancel',{method:'POST',
        headers:{'Content-Type':'application/json'},
        body:JSON.stringify({activityId,journalId})})
      if(!response.ok){
        setPreviewResult('Annuleren niet mogelijk. Controleer de journalstatus voordat je verdergaat.')
        return
      }
      setWritePlan(null);setPreparedBackupId(null);setWriteAttempted(false)
      setPreviewResult('Opslagvoorbereiding geannuleerd. Er is niets naar Garmin geschreven; de veiligheidskopie blijft bewaard.')
    }catch{setPreviewResult('Annuleren niet bevestigd. Controleer de journalstatus; probeer niet opnieuw op te slaan.')}
  }
  async function executeWrite(){
    if(!writePlan||writing||writeAttempted)return
    const activityId=Number(selectedGarmin)
    const confirmation=`APPROVE GARMIN ${activityId} ${writePlan.journalId}`
    setWriting(true);setWriteAttempted(true)
    try{
      const response=await fetch('/api/sync/write',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({
        activityId,journalId:writePlan.journalId,candidateSha256:writePlan.candidateSha256,
        outgoingSha256:writePlan.outgoingSha256,confirmation
      })})
      const result=await response.json().catch(()=>null) as {status?:string;error?:string}|null
      if(!response.ok){setPreviewResult(result?.error??`Garmin-opslag geweigerd (HTTP ${response.status}).`);return}
      setPreviewResult(result?.status==='Verified'
        ? 'Wijzigingen opgeslagen in Garmin en gecontroleerd. Je lokale gegevens zijn bijgewerkt.'
        : 'Garmin-opslag gaf geen bevestigde eindstatus.')
      if(result?.status==='Verified'){setDraft({});setWritePlan(null);setWriteAttempted(false);setCacheRevision(v=>v+1)}
    }catch{setPreviewResult('Garmin-opslag niet bereikbaar. Controleer het journal voordat je opnieuw iets doet.')}
    finally{setWriting(false)}
  }
  function pickEgym(id:string){setPreparedBackupId(null);setWritePlan(null);setSelectedEgym(id);const source=egym.find(e=>e.id===id);setSelectedGarmin(mode==='demo'?(demoGarmin.find(g=>g.completedAt?.slice(0,10)===source?.completedAt?.slice(0,10))?.id??''):propose(source,garmin));setDraft({});setReview(false)}
  async function readCache(){
    const [eResponse,gResponse,statusResponse]=await Promise.all([
      fetch('/api/cache/sessions?source=EGYM'),
      fetch('/api/cache/sessions?source=Garmin'),
      fetch('/api/cache/status')
    ])
    if(!eResponse.ok||!gResponse.ok||!statusResponse.ok)
      throw new Error('Lokale database niet beschikbaar. Controleer of de API draait.')
    const e=await eResponse.json() as Session[]
    const g=await gResponse.json() as Session[]
    const status=await statusResponse.json() as {checkpoints:{source:string,lastSuccessfulUtc:string}[]}
    setEgym(e);setGarmin(g);setMode('live')
    setSelectedEgym(e[0]?.id??'')
    setSelectedGarmin(propose(e[0],g))
    setLastSync(status.checkpoints.find(c=>c.source==='EGYM')?.lastSuccessfulUtc??null)
    setDraft({});setReview(false);setPreparedBackupId(null);setWritePlan(null);
  }
  useEffect(()=>{readCache().catch(e=>setError(e instanceof Error?e.message:'Kan lokale database niet lezen'))},[])
  async function loadEgym(){
    setLoading(true);setError('')
    try{
      const response=await fetch('/api/cache/sync',{method:'POST'})
      if(!response.ok)throw new Error(`Synchronisatie mislukt (HTTP ${response.status}).`)
      await readCache()
    }catch(e){setError(e instanceof Error?e.message:'Synchronisatie mislukt')}
    finally{setLoading(false)}
  }
  return <main className="app">
    <header className="topbar">
      <div className="brand"><div className="brandIcon"><Dumbbell size={22}/></div><div><strong>EGYM <span>Garmin Sync</span></strong><small>Trainingen vergelijken en synchroniseren</small></div></div>
      <div className="status"><span className="dot"/> {mode==='demo'?'Demomodus':'Lokale trainingsgegevens'}</div>
    </header>
    <section className="heading"><div><div className="eyebrow">TRAININGEN</div><h1>Sessies vergelijken</h1><p>Kies je training, bekijk de verschillen en neem gewenste aanpassingen over.</p></div><button className="primary" disabled={loading} onClick={loadEgym}><CloudDownload size={17}/>{loading?'Synchroniseren...':'Synchroniseren'}</button></section>
    {lastSync&&<p className="note">Laatste synchronisatie: {new Date(lastSync).toLocaleString('nl-NL')}</p>}
    {error && <div className="warning"><CircleAlert size={17}/>{error}</div>}
    {mode==='demo'&&<div className="notice"><CircleAlert size={17}/> Dit is een interactieve demo met voorbeeldgegevens. Live Garmin-data en synchronisatie zijn nog niet aangesloten.</div>}
    {mode==='live'&&<div className="syncOverview"><div><strong>{statusCounts.different} trainingen met afwijkingen</strong><span>{statusCounts.matching} komen overeen · {statusCounts.pending} nog te controleren (laatste 20 trainingen, lokale cache)</span></div><button className="secondary" disabled={statusCounts.different===0} onClick={nextDifferent}>Volgende met afwijkingen <ArrowRight size={15}/></button></div>}
    <section className="sessionGrid">
      <article className="panel"><div className="panelHead"><div><div className="provider"><span className="providerIcon">E</span> EGYM <span className="light">· bron</span></div><h2>Selecteer een training</h2></div><span className="count">{egym.length} sessies</span></div><label className="label" htmlFor="egymSession">EGYM-training kiezen</label><select id="egymSession" value={selectedEgym} onChange={e=>pickEgym(e.target.value)}><option value="">Geen training geselecteerd</option>{egym.map(s=><option key={s.id} value={s.id}>{displayDate(s.completedAt)} · {s.exerciseCount} oefeningen {statusLabel(s.id)}</option>)}</select></article>
      <article className="panel"><div className="panelHead"><div><div className="provider"><span className="providerIcon garmin">G</span> GARMIN <span className="light">· doel</span></div><h2>Bijbehorende training</h2></div><Link2 size={18} className="subtle"/></div><div className="suggest"><strong>Voorgestelde match</strong><span>{mode==='demo'?'Op datum gekoppeld (demo)':candidate?'Unieke match op methode en tijd':'Geen eenduidige match — kies handmatig'}</span></div><label className="label" htmlFor="garminSession">Garmin-training kiezen</label><select id="garminSession" value={selectedGarmin} onChange={e=>{setSelectedGarmin(e.target.value);setDraft({});setReview(false);setPreparedBackupId(null);setWritePlan(null);}} ><option value="">Geen training geselecteerd</option>{garmin.map(s=><option key={s.id} value={s.id}>{displayDate(s.completedAt)} · {s.id}</option>)}</select><p className="note">Je kunt een voorgestelde match altijd handmatig wijzigen. Alleen een unieke kandidaat binnen twee uur met passende trainingsmethode wordt voorgesteld.</p></article>
    </section>
    
    <section className="comparison panel"><div className="compareHeader"><div><div className="eyebrow">VERGELIJKING</div><h2>Oefeningen en sets</h2><p>Neem gewicht en herhalingen afzonderlijk over van EGYM naar Garmin.</p></div><div className="compareTools"><span className="count">{differenceCount} afwijkende sets · {changeList.length} wijzigingen gekozen</span><label className="differenceFilter"><input type="checkbox" checked={differencesOnly} onChange={e=>setDifferencesOnly(e.target.checked)}/> Alleen afwijkingen</label></div></div>
      {setsError&&<div className="warning">{setsError}</div>}
      {warnings.length>0&&<div className="notice">{warnings.join(' · ')}</div>}
      {setsLoading&&<div className="empty">Oefeningen laden...</div>}
      {selectedGarmin&&!setsLoading&&displayedRows.length>0?<><div className="tableScroll"><table><thead><tr><th>EGYM-oefening</th><th className="num">EGYM <span>(bron)</span></th><th className="action">Overnemen</th><th className="num">Garmin <span>(concept)</span></th><th>Garmin-oefening</th></tr></thead><tbody>{visibleRows.map(row=><tr key={row.id} className={differs(row)?'diffRow':'matchRow'}><td><strong>{row.egymExercise??row.exercise}</strong><small>Set {row.set}</small></td><td className="num values"><div className={row.egymKg!==row.garminKg?'differenceValue':''}>{row.egymKg} kg</div><div className={row.egymReps!==row.garminReps?'differenceValue':''}>{row.egymReps} reps</div></td><td className="action"><div><button aria-label={`Neem gewicht van ${row.exercise} set ${row.set} over`} disabled={(draft[row.id]?.kg??row.garminKg)===row.egymKg} onClick={()=>stage(row.id,'kg',row.egymKg)}>kg <ArrowRight size={14}/></button><button aria-label={`Neem herhalingen van ${row.exercise} set ${row.set} over`} disabled={(draft[row.id]?.reps??row.garminReps)===row.egymReps} onClick={()=>stage(row.id,'reps',row.egymReps)}>reps <ArrowRight size={14}/></button></div></td><td className="num values"><div className={draft[row.id]?.kg!==undefined?'staged':''}>{draft[row.id]?.kg??row.garminKg} kg</div><div className={draft[row.id]?.reps!==undefined?'staged':''}>{draft[row.id]?.reps??row.garminReps} reps</div></td><td><strong>{row.garminExercise??row.exercise}</strong><small>Set {row.set}</small></td></tr>)}</tbody></table></div>
      <div className="footer"><div className="buttons"><button className="secondary" onClick={()=>{setDraft(Object.fromEntries(displayedRows.map(r=>[r.id,{kg:r.egymKg,reps:r.egymReps}])));setReview(false);setPreparedBackupId(null);setWritePlan(null);}}><RefreshCw size={16}/> Alles overnemen</button><button className="ghost" onClick={()=>{setDraft({});setReview(false);setPreparedBackupId(null);setWritePlan(null);}}><Undo2 size={16}/> Ongedaan maken</button></div><button className="primary" disabled={changeList.length===0} onClick={()=>void checkServerPreview()}>Wijzigingen controleren <ArrowRight size={16}/></button></div>
      {review&&<div className="review"><h3>Te bewaren wijzigingen ({changeList.length})</h3>{changeList.map((v,i)=><p key={i}>{v}</p>)}{previewResult&&<p>{previewResult}</p>}{mode==='live'&&selectedGarmin&&writePlan===null&&<button className="ghost" onClick={()=>void resumeWritePlan()}>Bestaande voorbereiding terughalen</button>}{mode==='live'&&changeList.length>0&&writePlan===null&&<button className="primary" disabled={preparing||writing} onClick={()=>void prepareSave()}><Save size={16}/>{preparing?'Veiligheidscontrole uitvoeren...':'Opslaan in Garmin'}</button>}{writePlan&&<><div className="saveConfirm" role="group" aria-label="Garmin-opslag bevestigen"><h3>Wijzigingen opslaan in Garmin?</h3><p>Alleen de hierboven getoonde wijzigingen worden opgeslagen. De volledige Garmin-training wordt daarna gecontroleerd.</p><div className="buttons"><button className="primary" disabled={writing||writeAttempted||preparing||!writePlan.canWrite} onClick={()=>void executeWrite()}><Save size={16}/>{writing?'Opslaan en controleren...':'Ja, opslaan'}</button><button className="ghost" disabled={writing||preparing} onClick={()=>void cancelWrite()}>Nee, annuleren</button></div></div>{writeAttempted&&<p>Deze opslagpoging kan niet opnieuw worden verzonden. Controleer eerst de journalstatus.</p>}{!writePlan.canWrite&&<p>Opslaan is momenteel uitgeschakeld. Annuleren blijft mogelijk.</p>}</>}<p>Na je bevestiging controleren we automatisch of Garmin alle wijzigingen correct heeft opgeslagen.</p></div>}</>:<div className="empty"><CircleAlert size={23}/><strong>Geen vergelijkbare sets beschikbaar</strong><p>Selecteer links en rechts een training. Niet-herkende of incomplete sets worden niet automatisch gekoppeld.</p></div>}
    </section>
  </main>
}
export default App
