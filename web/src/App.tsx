import { useMemo, useState } from 'react'
import { ArrowRight, CircleAlert, CloudDownload, Dumbbell, Link2, RefreshCw, Save, Undo2 } from 'lucide-react'
import './style.css'

type Session = { id: string; completedAt: string | null; exerciseCount: number; method?: string | null; name?: string }
type SetRow = { id: string; exercise: string; set: number; egymKg: number; garminKg: number; egymReps: number; garminReps: number }
type Draft = Record<string, { kg?: number; reps?: number }>
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
  const [error,setError] = useState('')
  const [loading,setLoading] = useState(false)
  const [review,setReview] = useState(false)
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
  const changeList = useMemo(()=>exampleRows.flatMap(row=>{
    const state = draft[row.id] ?? {}
    const result: string[]=[]
    if (state.kg!==undefined && state.kg!==row.garminKg) result.push(`${row.exercise} set ${row.set}: ${row.garminKg} → ${state.kg} kg`)
    if (state.reps!==undefined && state.reps!==row.garminReps) result.push(`${row.exercise} set ${row.set}: ${row.garminReps} → ${state.reps} reps`)
    return result
  }),[draft])
  function stage(id:string,key:'kg'|'reps',value:number){setDraft(p=>({...p,[id]:{...p[id],[key]:value}}));setReview(false)}
  function pickEgym(id:string){setSelectedEgym(id);const source=egym.find(e=>e.id===id);setSelectedGarmin(mode==='demo'?(demoGarmin.find(g=>g.completedAt?.slice(0,10)===source?.completedAt?.slice(0,10))?.id??''):propose(source,garmin));setDraft({});setReview(false)}
  async function loadEgym(){
    setLoading(true);setError('')
    try {
      const response=await fetch('/api/egym/sessions?days=180')
      if(!response.ok)throw new Error(`EGYM ophalen niet beschikbaar (HTTP ${response.status}). Controleer de lokale API en serverconfiguratie.`)
      const data=(await response.json() as Session[]).sort((a,b)=>{
        const left=a.completedAt ? Date.parse(a.completedAt) : NaN
        const right=b.completedAt ? Date.parse(b.completedAt) : NaN
        const la=Number.isFinite(left)?left:-Infinity
        const rb=Number.isFinite(right)?right:-Infinity
        return rb-la
      })
      const garminResponse=await fetch('/api/garmin/sessions')
      if(!garminResponse.ok) throw new Error(`Garmin ophalen niet beschikbaar (HTTP ${garminResponse.status}). De demomodus blijft actief.`)
      const garminData=await garminResponse.json() as Session[]
      setEgym(data);setGarmin(garminData);setMode('live');setSelectedEgym(data[0]?.id??'');setSelectedGarmin(propose(data[0],garminData));setDraft({});setReview(false)
    }catch(e){setError(e instanceof Error?e.message:'Onbekende fout')}
    finally{setLoading(false)}
  }
  return <main className="app">
    <header className="topbar">
      <div className="brand"><div className="brandIcon"><Dumbbell size={22}/></div><div><strong>EGYM <span>Garmin Sync</span></strong><small>Trainingen vergelijken en synchroniseren</small></div></div>
      <div className="status"><span className="dot"/> {mode==='demo'?'Demomodus':'EGYM + Garmin verbonden · read-only'}</div>
    </header>
    <section className="heading"><div><div className="eyebrow">TRAININGEN</div><h1>Sessies vergelijken</h1><p>Kies een EGYM-training en controleer de voorgestelde Garmin-match.</p></div><button className="primary" disabled={loading} onClick={loadEgym}><CloudDownload size={17}/>{loading?'Ophalen...':'EGYM-sessies laden'}</button></section>
    {error && <div className="warning"><CircleAlert size={17}/>{error}</div>}
    {mode==='demo'&&<div className="notice"><CircleAlert size={17}/> Dit is een interactieve demo met voorbeeldgegevens. Live Garmin-data en synchronisatie zijn nog niet aangesloten.</div>}
    <section className="sessionGrid">
      <article className="panel"><div className="panelHead"><div><div className="provider"><span className="providerIcon">E</span> EGYM <span className="light">· bron</span></div><h2>Selecteer een training</h2></div><span className="count">{egym.length} sessies</span></div><label className="label" htmlFor="egymSession">EGYM-training kiezen</label><select id="egymSession" value={selectedEgym} onChange={e=>pickEgym(e.target.value)}><option value="">Geen training geselecteerd</option>{egym.map(s=><option key={s.id} value={s.id}>{displayDate(s.completedAt)} · {s.exerciseCount} oefeningen</option>)}</select></article>
      <article className="panel"><div className="panelHead"><div><div className="provider"><span className="providerIcon garmin">G</span> GARMIN <span className="light">· doel</span></div><h2>Bijbehorende training</h2></div><Link2 size={18} className="subtle"/></div><div className="suggest"><strong>Voorgestelde match</strong><span>{mode==='demo'?'Op datum gekoppeld (demo)':candidate?'Unieke match op methode en tijd':'Geen eenduidige match — kies handmatig'}</span></div><label className="label" htmlFor="garminSession">Garmin-training kiezen</label><select id="garminSession" value={selectedGarmin} onChange={e=>{setSelectedGarmin(e.target.value);setDraft({});setReview(false)}} ><option value="">Geen training geselecteerd</option>{garmin.map(s=><option key={s.id} value={s.id}>{displayDate(s.completedAt)} · {s.id}</option>)}</select><p className="note">Je kunt een voorgestelde match altijd handmatig wijzigen. Alleen een unieke kandidaat binnen twee uur met passende trainingsmethode wordt voorgesteld.</p></article>
    </section>
    <section className="comparison panel"><div className="compareHeader"><div><div className="eyebrow">VERGELIJKING</div><h2>Oefeningen en sets</h2><p>Neem gewicht en herhalingen afzonderlijk over van EGYM naar Garmin.</p></div><span className="count">{changeList.length} voorbereid</span></div>
      {mode==='demo'&&selectedGarmin?<><div className="tableScroll"><table><thead><tr><th>Oefening</th><th className="num">EGYM <span>(bron)</span></th><th className="action">Overnemen</th><th className="num">Garmin <span>(concept)</span></th></tr></thead><tbody>{exampleRows.map(row=><tr key={row.id}><td><strong>{row.exercise}</strong><small>Set {row.set}</small></td><td className="num values"><div>{row.egymKg} kg</div><div>{row.egymReps} reps</div></td><td className="action"><div><button aria-label={`Neem gewicht van ${row.exercise} set ${row.set} over`} disabled={(draft[row.id]?.kg??row.garminKg)===row.egymKg} onClick={()=>stage(row.id,'kg',row.egymKg)}>kg <ArrowRight size={14}/></button><button aria-label={`Neem herhalingen van ${row.exercise} set ${row.set} over`} disabled={(draft[row.id]?.reps??row.garminReps)===row.egymReps} onClick={()=>stage(row.id,'reps',row.egymReps)}>reps <ArrowRight size={14}/></button></div></td><td className="num values"><div className={draft[row.id]?.kg!==undefined?'staged':''}>{draft[row.id]?.kg??row.garminKg} kg</div><div className={draft[row.id]?.reps!==undefined?'staged':''}>{draft[row.id]?.reps??row.garminReps} reps</div></td></tr>)}</tbody></table></div>
      <div className="footer"><div className="buttons"><button className="secondary" onClick={()=>{setDraft(Object.fromEntries(exampleRows.map(r=>[r.id,{kg:r.egymKg,reps:r.egymReps}])));setReview(false)}}><RefreshCw size={16}/> Alles overnemen</button><button className="ghost" onClick={()=>{setDraft({});setReview(false)}}><Undo2 size={16}/> Ongedaan maken</button></div><button className="primary" disabled={changeList.length===0} onClick={()=>setReview(true)}>Wijzigingen controleren <ArrowRight size={16}/></button></div>
      {review&&<div className="review"><h3>Voorbereide wijzigingen ({changeList.length})</h3>{changeList.map((v,i)=><p key={i}>{v}</p>)}<button disabled className="primary"><Save size={16}/> Opslaan in Garmin (nog niet beschikbaar)</button><p>Dit is een demo. Er wordt niets naar Garmin gestuurd.</p></div>}</>:<div className="empty"><CircleAlert size={23}/><strong>Vergelijking met echte sets volgt</strong><p>EGYM- en Garmin-sessies kunnen nu worden geselecteerd. Het uitlezen en vergelijken van afzonderlijke sets volgt; opslaan blijft uitgeschakeld.</p></div>}
    </section>
  </main>
}
export default App
