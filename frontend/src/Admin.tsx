import { ProjectManager } from './Projects'
import type { useProjects } from './projectModel'
import './Admin.css'

export default function Admin({ propertyName, projectsState }: {
  propertyName: string
  projectsState: ReturnType<typeof useProjects>
}) {
  return <section className="admin-page" aria-label="Administration">
    <div className="admin-intro">
      <p className="admin-breadcrumb">Admin <span>/</span> Projects</p>
      <h2>Create a new project</h2>
      <p>Projects help you organize receipt items within <strong>{propertyName}</strong>. Create them here, then assign items in Review.</p>
    </div>
    <ProjectManager state={projectsState} />
  </section>
}
