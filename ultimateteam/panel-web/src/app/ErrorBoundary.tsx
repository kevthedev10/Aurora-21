import { Component, type ReactNode } from 'react';
import { ErrorPanel } from '../ui/Layout';

/** One page's crash stays in that page: the shell, menu and status keep working. The shell remounts it per route. */
export class PageErrorBoundary extends Component<{ children: ReactNode }, { error: unknown }> {
  override state: { error: unknown } = { error: null };

  static getDerivedStateFromError(error: unknown) {
    return { error };
  }

  override render() {
    if (this.state.error) {
      return <ErrorPanel what="This page" error={this.state.error} onRetry={() => this.setState({ error: null })} />;
    }
    return this.props.children;
  }
}
