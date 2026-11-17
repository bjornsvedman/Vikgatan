import { useState, type FormEvent } from 'react'
import { Check, FolderKanban, Pencil, X } from 'lucide-react'
import { type Project, unassignedProject, type useProjects } from './projectModel'
import './Projects.css'

export function ProjectSelect({ projects, value, onChange, label, all = false, disabled = false, invalid = false, placeholder }: {
  projects: Project[], value: string, onChange: (value: string) => void, label: string,
  all?: boolean, disabled?: boolean, invalid?: boolean, placeholder?: { value: string, label: string },
}) {
  return <div className="project-select">
    <label>{label}<select value={value} onChange={event => onChange(event.target.value)} disabled={disabled} aria-invalid={invalid || undefined}>
      {placeholder && <option value={placeholder.value}>{placeholder.label}</option>}
      {all && <option value="">All projects</option>}
      <option value={all ? unassignedProject : ''}>Unassigned</option>
      {projects.map(project => <option key={project.id} value={project.id}>{project.name}</option>)}
    </select></label>
  </div>
}

export function ProjectManager({ state }: {
  state: ReturnType<typeof useProjects>,
}) {
  const [name, setName] = useState('')
  const [renameId, setRenameId] = useState('')
  const [rename, setRename] = useState('')
  const [notice, setNotice] = useState('')
  async function createProject(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setNotice('')
    const project = await state.save(name.trim())
    if (project) { setName(''); setNotice(`Project "${project.name}" created.`) }
  }
  async function renameProject(event: FormEvent<HTMLFormElement>, id: string) {
    event.preventDefault()
    setNotice('')
    const project = await state.save(rename.trim(), id)
    if (project) {
      setRenameId('')
      setNotice(`Project renamed to "${project.name}". Saved item assignments are unchanged.`)
    }
  }
  function startRename(project: Project) {
    setNotice('')
    setRenameId(project.id)
    setRename(project.name)
  }
  return <div className="project-manager">
    {state.error && <p className="message message-error" role="alert">{state.error}</p>}
    {notice && <p className="project-manager-notice" role="status">{notice}</p>}
    <form className="project-create-form" onSubmit={event => void createProject(event)}>
      <label htmlFor="new-project-name">Project name</label>
      <div className="project-create-controls">
        <input id="new-project-name" autoComplete="off" maxLength={128} placeholder="e.g. Kitchen renovation"
          value={name} onChange={event => setName(event.target.value)} disabled={state.loading || state.saving} />
        <button type="submit" className="button-primary" disabled={!name.trim() || state.loading || state.saving}>
          {state.saving ? 'Creating…' : 'Create project'}
        </button>
      </div>
      <p className="project-form-footnote">Projects belong to this property. You can assign a project to each receipt in Review.</p>
    </form>

    <section className="project-list" aria-labelledby="project-list-title">
      <div className="project-list-heading">
        <div><h3 id="project-list-title">Your projects</h3><p>Projects for this property</p></div>
        <span>{state.loading ? 'Loading…' : `${state.projects.length} ${state.projects.length === 1 ? 'project' : 'projects'}`}</span>
      </div>
      {state.loading ? <p className="project-list-empty">Loading projects…</p> : state.projects.length === 0 ? (
        <div className="project-list-empty"><FolderKanban size={20} aria-hidden="true" /><span>No projects yet. Create one above to get started.</span></div>
      ) : <ul>
        {state.projects.map(project => <li key={project.id}>
          {renameId === project.id ? (
            <form className="project-rename-form" onSubmit={event => void renameProject(event, project.id)}>
              <label className="sr-only" htmlFor={`rename-project-${project.id}`}>Rename {project.name}</label>
              <input id={`rename-project-${project.id}`} autoFocus maxLength={128} value={rename}
                onChange={event => setRename(event.target.value)} disabled={state.saving} />
              <button type="submit" className="project-row-action project-row-save" aria-label={`Save name for ${project.name}`}
                title="Save name" disabled={!rename.trim() || state.saving}><Check size={16} aria-hidden="true" /></button>
              <button type="button" className="project-row-action" aria-label="Cancel rename" title="Cancel"
                disabled={state.saving} onClick={() => setRenameId('')}><X size={16} aria-hidden="true" /></button>
            </form>
          ) : <>
            <span className="project-row-name"><FolderKanban size={16} aria-hidden="true" />{project.name}</span>
            <button type="button" className="project-row-action" onClick={() => startRename(project)}
              aria-label={`Rename ${project.name}`} title="Rename project" disabled={state.saving}>
              <Pencil size={15} aria-hidden="true" /><span>Rename</span>
            </button>
          </>}
        </li>)}
      </ul>}
    </section>
  </div>
}
