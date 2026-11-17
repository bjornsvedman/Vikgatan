import { useEffect, useRef, useState } from 'react'
import type { Receipt } from './ReviewDesk'

export type Project = { id: string, name: string }
export const unassignedProject = 'unassigned'

export function projectParameters(selection: string) {
  const query = new URLSearchParams()
  if (selection === unassignedProject) query.set('unassignedProject', 'true')
  else if (selection) query.set('projectId', selection)
  return query.toString()
}

export function receiptProjects(receipt: Receipt) {
  return [...new Set(receipt.lineItems.map(item => item.projectName ?? 'Unassigned'))].sort().join(', ') || 'Unassigned'
}

export function useProjects(propertyId: string, token: string) {
  const [projects, setProjects] = useState<Project[]>([])
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const request = useRef<AbortController | null>(null)
  useEffect(() => {
    const controller = new AbortController()
    request.current = controller
    if (propertyId) {
      void (async () => {
        setLoading(true); setError(''); setProjects([])
        try {
          const response = await fetch(`/api/properties/${encodeURIComponent(propertyId)}/projects/`, { signal: controller.signal })
          if (!response.ok) throw await projectError(response)
          const values = await response.json() as Project[]
          if (!controller.signal.aborted) setProjects(values)
        } catch (failure) {
          if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'Could not load projects.')
        } finally { if (!controller.signal.aborted) setLoading(false) }
      })()
    }
    return () => controller.abort()
  }, [propertyId])

  async function save(name: string, id?: string) {
    const controller = request.current
    if (!controller || controller.signal.aborted) return null
    setSaving(true); setError('')
    try {
      const response = await fetch(`/api/properties/${encodeURIComponent(propertyId)}/projects/${id ?? ''}`, {
        method: id ? 'PUT' : 'POST',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token },
        body: JSON.stringify({ name }), signal: controller.signal,
      })
      if (!response.ok) throw await projectError(response)
      const project = await response.json() as Project
      if (controller.signal.aborted) return null
      setProjects(current => [...current.filter(value => value.id !== project.id), project].sort((a, b) => a.name.localeCompare(b.name)))
      return project
    } catch (failure) {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'Could not save project.')
      return null
    } finally { if (!controller.signal.aborted) setSaving(false) }
  }
  return { projects, error, loading, saving, save }
}

async function projectError(response: Response) {
  const problem = await response.json() as { detail?: string, title?: string }
  return new Error(problem.detail ?? problem.title ?? `Project request failed (${response.status}).`)
}
